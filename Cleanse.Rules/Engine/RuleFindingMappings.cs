using Cleanse.Core.Domain;
using Cleanse.Rules.Domain;

namespace Cleanse.Rules.Engine;

internal static class RuleFindingMappings
{
    /// <summary>
    /// Turns a finding into a Core detection. The single place the descriptor is flattened onto
    /// the issue, so every issue carries the rule number, error code and error description in
    /// the same keys no matter which rule raised it.
    /// </summary>
    public static IssueDetection ToDetection(this RuleFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var context = new Dictionary<string, string>(finding.Context.Count + 4, StringComparer.Ordinal)
        {
            ["cph"] = finding.Cph.Value,
            ["ruleNumber"] = finding.Descriptor.RuleNumber,
            ["errorCode"] = finding.Descriptor.ErrorCode,
            ["errorDescription"] = finding.Descriptor.ErrorDescription
        };

        foreach (var (key, value) in finding.Context)
        {
            context[key] = value;
        }

        return new IssueDetection(finding.IssueId, finding.Descriptor.RuleId, context);
    }
}
