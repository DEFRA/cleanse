using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Test.Support;

/// <summary>
/// A rule, as the real engine will eventually define it. It lives in the test project on
/// purpose: Core knows nothing about rules, it only accepts the detections they emit.
/// </summary>
internal interface ICleanseRule
{
    string RuleId { get; }

    IEnumerable<IssueDetection> Evaluate();
}

/// <summary>
/// Stands in for the real overnight engine: open a run, stream detections in batches, then
/// close the run so the sweep deactivates anything the rules no longer report. A rule that
/// throws abandons the run instead of completing it, because a group-wide sweep after a partial
/// pass would wrongly close every issue the failed rule owns.
/// </summary>
internal sealed class FakeRulesEngine(ICleanseDispatcher dispatcher, string groupId, int batchSize = 100)
{
    public async Task<RuleRunDto> RunAsync(
        IEnumerable<ICleanseRule> rules,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        var start = await dispatcher.SendAsync(
            new StartRunCommand(groupId, Actor.System, runId), cancellationToken);

        if (start.IsFailure)
        {
            throw new InvalidOperationException($"Could not start a run: {start.Error}");
        }

        var run = start.Value;

        try
        {
            foreach (var batch in Evaluate(rules).Chunk(batchSize))
            {
                var upsert = await dispatcher.SendAsync(
                    new UpsertDetectionsCommand(run.RunId, batch, Actor.System), cancellationToken);

                if (upsert.IsFailure)
                {
                    throw new InvalidOperationException($"Upsert failed: {upsert.Error}");
                }
            }
        }
        catch (Exception ex)
        {
            await dispatcher.SendAsync(
                new AbandonRunCommand(run.RunId, ex.Message, Actor.System), CancellationToken.None);
            throw;
        }

        var complete = await dispatcher.SendAsync(new CompleteRunCommand(run.RunId, Actor.System), cancellationToken);
        if (complete.IsFailure)
        {
            throw new InvalidOperationException($"Could not complete the run: {complete.Error}");
        }

        return complete.Value;
    }

    private static IEnumerable<IssueDetection> Evaluate(IEnumerable<ICleanseRule> rules) =>
        rules.SelectMany(rule => rule.Evaluate());
}

internal sealed record Holding(string Cph, string Keeper);

/// <summary>Rule 2: every holding in the legacy extract must exist in the new system.</summary>
internal sealed class HoldingMissingInTargetRule(
    IReadOnlyCollection<Holding> source,
    IReadOnlyCollection<Holding> target) : ICleanseRule
{
    public string RuleId => TestData.Rule2;

    public IEnumerable<IssueDetection> Evaluate()
    {
        var targetCphs = target.Select(h => h.Cph).ToHashSet(StringComparer.Ordinal);

        foreach (var holding in source.Where(h => !targetCphs.Contains(h.Cph)))
        {
            yield return new IssueDetection(
                IssueId.Derive(RuleId, holding.Cph),
                RuleId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cph"] = holding.Cph,
                    ["keeper"] = holding.Keeper
                });
        }
    }
}

/// <summary>Rule 5: the keeper name must agree across both systems.</summary>
internal sealed class KeeperMismatchRule(
    IReadOnlyCollection<Holding> source,
    IReadOnlyCollection<Holding> target) : ICleanseRule
{
    public string RuleId => TestData.Rule5;

    public IEnumerable<IssueDetection> Evaluate()
    {
        var byCph = target.ToDictionary(h => h.Cph, StringComparer.Ordinal);

        foreach (var holding in source)
        {
            if (!byCph.TryGetValue(holding.Cph, out var match) ||
                string.Equals(match.Keeper, holding.Keeper, StringComparison.Ordinal))
            {
                continue;
            }

            yield return new IssueDetection(
                IssueId.Derive(RuleId, holding.Cph),
                RuleId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cph"] = holding.Cph,
                    ["source.keeper"] = holding.Keeper,
                    ["target.keeper"] = match.Keeper
                });
        }
    }
}

internal sealed class ThrowingRule(string ruleId = "rule-boom") : ICleanseRule
{
    public string RuleId => ruleId;

    public IEnumerable<IssueDetection> Evaluate()
    {
        yield return new IssueDetection(IssueId.Derive(RuleId, "before-the-bang"), RuleId);
        throw new InvalidOperationException("source system timed out");
    }
}
