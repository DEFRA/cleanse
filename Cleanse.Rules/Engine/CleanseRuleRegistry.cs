using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam;
using Cleanse.Rules.Groups.CtsSam.Rules;

namespace Cleanse.Rules.Engine;

/// <summary>
/// The rule book: the one place that answers which rules exist, in what order they run, and
/// which are switched off.
/// </summary>
public sealed class CleanseRuleRegistry
{
    /// <summary>The only rule group that exists today.</summary>
    public const string CtsSamGroupId = "cts-sam";

    private readonly IReadOnlyList<ICleanseRule> _all;

    /// <remarks>
    /// The rules are a required argument rather than one that falls back to a default. The
    /// Microsoft container resolves an unregistered <c>IEnumerable&lt;ICleanseRule&gt;</c> to an
    /// empty sequence rather than to null, so a defaulted parameter would leave the registry
    /// silently empty and every run would finish having found nothing at all.
    /// </remarks>
    public CleanseRuleRegistry(IEnumerable<ICleanseRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _all =
        [
            .. rules
                .OrderBy(rule => rule.Priority)
                .ThenBy(rule => rule.Descriptor.RuleNumber, StringComparer.Ordinal)
        ];

        if (_all.Count == 0)
        {
            throw new ArgumentException("The rule registry cannot be constructed without rules.", nameof(rules));
        }

        var duplicate = _all
            .GroupBy(rule => rule.Descriptor.RuleId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Rule id '{duplicate.Key}' is registered more than once; issue keys would collide.",
                nameof(rules));
        }

        Active = [.. _all.Where(rule => rule.Status == RuleStatus.Active)];
    }

    /// <summary>Every known rule in execution order, including those on hold.</summary>
    public IReadOnlyList<ICleanseRule> All => _all;

    /// <summary>The rules a run actually evaluates, in execution order.</summary>
    public IReadOnlyList<ICleanseRule> Active { get; }

    public static IEnumerable<ICleanseRule> CreateDefaultRules() =>
    [
        new Rule02CtsCphNotInSam(),
        new Rule01SamNoCattleUnit()
    ];

    public ICleanseRule? ByNumber(string ruleNumber) =>
        _all.FirstOrDefault(rule =>
            string.Equals(rule.Descriptor.RuleNumber, ruleNumber, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Evaluates the record against every active rule, in order. Rules that do not apply are
    /// skipped rather than asked, which is what makes a subordinate rule stay silent when the
    /// rule it depends on has fired.
    /// </summary>
    public List<RuleFinding> Evaluate(CtsSamRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<RuleFinding>();
        foreach (var rule in Active)
        {
            if (!rule.AppliesTo(context))
            {
                continue;
            }

            var finding = rule.Evaluate(context);
            if (finding is not null)
            {
                findings.Add(finding);
            }
        }

        return findings;
    }
}
