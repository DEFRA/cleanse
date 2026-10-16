using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam;

namespace Cleanse.Rules.Abstractions;

/// <summary>
/// A single, self contained cleanse rule.
/// </summary>
/// <remarks>
/// Everything needed to understand a rule lives in the file that implements this interface: its
/// identity, when it runs, what it requires, and the check itself. Subordination between rules
/// is expressed through <see cref="AppliesTo"/> rather than through branching in the engine, so
/// adding a rule never means editing the engine.
/// </remarks>
public interface ICleanseRule
{
    RuleDescriptor Descriptor { get; }

    /// <summary>Evaluation order. Lower runs first.</summary>
    int Priority { get; }

    RuleStatus Status { get; }

    /// <summary>
    /// Whether this rule has anything to say about the record. Returning false means the rule is
    /// silent, which is how a rule declares that a more fundamental rule must have passed first.
    /// </summary>
    bool AppliesTo(CtsSamRuleContext context);

    /// <summary>Evaluates a record that <see cref="AppliesTo"/> accepted. Null means no issue.</summary>
    RuleFinding? Evaluate(CtsSamRuleContext context);
}
