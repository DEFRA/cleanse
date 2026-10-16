using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam;

namespace Cleanse.Rules.Groups.CtsSam.Rules;

/// <summary>
/// RULE 1, PRIORITY 2. The SAM holding declares no cattle unit.
/// </summary>
/// <remarks>
/// Subordinate to rule 2: it requires the CPH to exist on both sides, which rule 2 is what
/// establishes. An issue is raised when none of the holding's animal profiles record bison,
/// cattle or buffalo.
/// </remarks>
public sealed class Rule01SamNoCattleUnit : ICleanseRule
{
    /// <summary>Bison, cattle and buffalo. Any one of these makes the holding a cattle unit.</summary>
    public static readonly IReadOnlySet<string> CattleSpeciesCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BSN", "CTT", "BFF" };

    public static readonly RuleDescriptor Rule = new(
        ruleId: "1",
        ruleNumber: "1",
        name: "No cattle unit in Sam",
        errorCode: "01",
        errorDescription: "No-cattle-unit-in-Sam",
        summary: "The SAM Holding record has no animal species code of Bison, Cattle or Buffalo (BSN, CTT, BFF).");

    public RuleDescriptor Descriptor => Rule;

    public int Priority => 2;

    public RuleStatus Status => RuleStatus.Active;

    public bool AppliesTo(CtsSamRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ExistsInSam;
    }

    public RuleFinding? Evaluate(CtsSamRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var codes = context.Record.SamSpeciesCodes;
        if (codes.Any(CattleSpeciesCodes.Contains))
        {
            return null;
        }

        return context.Issue(Descriptor, values =>
        {
            values["samHoldingId"] = context.Record.SamHoldingId!;

            if (!string.IsNullOrWhiteSpace(context.Record.SamFeatureName))
            {
                values["samFeatureName"] = context.Record.SamFeatureName.Trim();
            }

            if (codes.Count > 0)
            {
                values["samSpeciesCodes"] = string.Join(",", codes.OrderBy(c => c, StringComparer.Ordinal));
            }
        });
    }
}
