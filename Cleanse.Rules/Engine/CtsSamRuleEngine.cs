using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Runs;
using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam;
using Microsoft.Extensions.Logging;

namespace Cleanse.Rules.Engine;

/// <summary>
/// Drives one pass of the <c>cts-sam</c> rule group over a data snapshot.
/// </summary>
/// <remarks>
/// The engine contains no rule logic and no persistence logic: it streams records, asks the
/// registry what it thinks, and hands findings to Core. Adding a rule never means editing this
/// file. The run must already exist — the caller opens it, so that the single-run-per-group
/// guarantee is settled before any work begins.
/// </remarks>
public sealed class CtsSamRuleEngine(
    ICleanseDispatcher dispatcher,
    ICtsSamDataSourceFactory dataSourceFactory,
    CleanseRuleRegistry registry,
    CleanseRulesOptions options,
    ILogger<CtsSamRuleEngine> logger)
{
    public async Task<RuleEngineResult> ExecuteAsync(
        string runId,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(actor);

        try
        {
            return await RunAsync(runId, actor, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a user cancellation. Release the group's slot rather than leaving
            // the run wedged as running until the reaper notices.
            logger.LogWarning("Run {RunId} was interrupted by shutdown", runId);
            await SafeAsync(dispatcher.SendAsync(
                new AbandonRunCommand(runId, "The host shut down mid-run.", actor), CancellationToken.None));

            return Result(runId, RuleEngineOutcome.Cancelled, Tally.Empty, "The host shut down mid-run.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId} failed", runId);
            await SafeAsync(dispatcher.SendAsync(
                new FailRunCommand(runId, ex.Message, ex.ToString(), actor), CancellationToken.None));

            return Result(runId, RuleEngineOutcome.Failed, Tally.Empty, ex.Message);
        }
    }

    private async Task<RuleEngineResult> RunAsync(string runId, Actor actor, CancellationToken cancellationToken)
    {
        var tally = new Tally();

        await ReportAsync(runId, $"Acquiring snapshot ({dataSourceFactory.Description})", 0, 0, cancellationToken);

        await using var source = await dataSourceFactory.OpenAsync(cancellationToken);

        var total = await source.CountRecordsAsync(cancellationToken);
        logger.LogInformation("Run {RunId} will evaluate {Total} CTS open locations", runId, total);
        await ReportAsync(runId, "Evaluating rules", 0, total, cancellationToken);

        var batch = new List<RuleFinding>(options.BatchSize);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var record in source.StreamRecordsAsync(cancellationToken))
        {
            var context = new CtsSamRuleContext(record);
            foreach (var finding in registry.Evaluate(context))
            {
                if (!seen.Add(finding.IssueId.Value))
                {
                    // Two CTS locations sharing a CPH would upsert onto one issue, so the second
                    // silently wins. Surface it rather than let the count quietly disagree.
                    logger.LogWarning(
                        "Run {RunId}: duplicate issue key {IssueId} from CTS location {LocationNumber}",
                        runId, finding.IssueId.Value, record.CtsLocationNumber);
                    continue;
                }

                batch.Add(finding);
            }

            tally.RecordsScanned++;

            if (tally.RecordsScanned % options.BatchSize != 0)
            {
                continue;
            }

            await FlushAsync(runId, actor, batch, tally, cancellationToken);
            await ReportAsync(runId, "Evaluating rules", tally.RecordsScanned, total, cancellationToken);

            if (await IsCancellationRequestedAsync(runId, cancellationToken))
            {
                return await CancelAsync(runId, actor, tally, cancellationToken);
            }
        }

        await FlushAsync(runId, actor, batch, tally, cancellationToken);
        await ReportAsync(runId, "Deactivating issues no longer detected", tally.RecordsScanned, total, cancellationToken);

        var completed = await dispatcher.SendAsync(new CompleteRunCommand(runId, actor), cancellationToken);
        if (completed.IsFailure)
        {
            throw new InvalidOperationException($"Could not complete run '{runId}': {completed.Error}");
        }

        tally.IssuesDeactivated = completed.Value.IssuesDeactivated;

        logger.LogInformation(
            "Run {RunId} completed: {Scanned} records, {Findings} findings, {Created} created, " +
            "{Reactivated} reactivated, {Deactivated} deactivated",
            runId, tally.RecordsScanned, tally.FindingsRaised, tally.IssuesCreated,
            tally.IssuesReactivated, tally.IssuesDeactivated);

        return Result(runId, RuleEngineOutcome.Completed, tally, null);
    }

    private async Task FlushAsync(
        string runId,
        Actor actor,
        List<RuleFinding> batch,
        Tally tally,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var detections = batch.Select(f => f.ToDetection()).ToArray();

        // Detections are the machine's work, so they are attributed to the machine even when a
        // person asked for the run.
        var result = await dispatcher.SendAsync(
            new UpsertDetectionsCommand(runId, detections, Actor.System), cancellationToken);

        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Could not upsert detections for run '{runId}': {result.Error}");
        }

        tally.FindingsRaised += batch.Count;
        tally.IssuesCreated += result.Value.Created;
        tally.IssuesReactivated += result.Value.Reactivated;
        batch.Clear();
    }

    private async Task ReportAsync(
        string runId,
        string stage,
        int processed,
        int total,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new ReportRunProgressCommand(runId, stage, processed, total), cancellationToken);

        if (result.IsFailure)
        {
            logger.LogWarning("Could not record progress for run {RunId}: {Error}", runId, result.Error);
        }
    }

    private async Task<bool> IsCancellationRequestedAsync(string runId, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetRunQuery(runId), cancellationToken);
        return result.IsSuccess && result.Value.CancellationRequested;
    }

    private async Task<RuleEngineResult> CancelAsync(
        string runId,
        Actor actor,
        Tally tally,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Run {RunId} cancelled after {Scanned} records", runId, tally.RecordsScanned);

        var cancelled = await dispatcher.SendAsync(
            new CancelRunCommand(runId, "Cancelled on request.", actor), cancellationToken);

        if (cancelled.IsFailure)
        {
            throw new InvalidOperationException($"Could not cancel run '{runId}': {cancelled.Error}");
        }

        return Result(runId, RuleEngineOutcome.Cancelled, tally, "Cancelled on request.");
    }

    private static RuleEngineResult Result(string runId, RuleEngineOutcome outcome, Tally tally, string? failure) =>
        new(runId, outcome, tally.RecordsScanned, tally.FindingsRaised, tally.IssuesCreated,
            tally.IssuesReactivated, tally.IssuesDeactivated, failure);

    private static async Task SafeAsync<T>(Task<T> task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // Already failing; a failure recording the failure must not replace the real one.
        }
    }

    private sealed class Tally
    {
        public static Tally Empty => new();

        public int RecordsScanned;
        public int FindingsRaised;
        public int IssuesCreated;
        public int IssuesReactivated;
        public int IssuesDeactivated;
    }
}
