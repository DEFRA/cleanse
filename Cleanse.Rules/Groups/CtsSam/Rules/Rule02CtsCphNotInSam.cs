using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam;

namespace Cleanse.Rules.Groups.CtsSam.Rules;

/// <summary>
/// RULE 2, PRIORITY 1. A CTS open location whose CPH is absent from the SAM holding dataset.
/// </summary>
/// <remarks>
/// The primary rule of the group. When it fires there is no SAM holding to compare against, so
/// every comparison rule is necessarily silent for that record; they say so themselves by
/// requiring <see cref="CtsSamRuleContext.ExistsInSam"/>.
/// <para>
/// Slaughterhouses are excluded upstream by the CPH format: CTS files them under a bare number
/// rather than an <c>nn/nnn/nnnn</c> CPH, so they never reach a rule.
/// </para>
/// </remarks>
public sealed class Rule02CtsCphNotInSam : ICleanseRule
{
    public static readonly RuleDescriptor Rule = new(
        ruleId: "2",
        ruleNumber: "2",
        name: "CTS CPH missing from Sam",
        errorCode: "02",
        errorDescription: "CTS-CPH-missing-in-Sam",
        summary: "A non-slaughterhouse CTS open location whose CPH is absent from the SAM Holding dataset.");

    public RuleDescriptor Descriptor => Rule;

    public int Priority => 1;

    public RuleStatus Status => RuleStatus.Active;

    public bool AppliesTo(CtsSamRuleContext context) => true;

    public RuleFinding? Evaluate(CtsSamRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ExistsInSam ? null : context.Issue(Descriptor);
    }
}
