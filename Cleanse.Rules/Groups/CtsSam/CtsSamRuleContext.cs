using Cleanse.Rules.Domain;

namespace Cleanse.Rules.Groups.CtsSam;

/// <summary>
/// Everything a <c>cts-sam</c> rule is allowed to see for the record under evaluation.
/// </summary>
/// <remarks>
/// Rules never reach back into a data source, which is what keeps them pure functions and
/// therefore directly unit testable. The context deliberately exposes raw SAM data rather than
/// pre-computed answers such as "has a cattle unit": deciding what the data means is the rule's
/// job, and putting it here would spread one rule's logic across two files.
/// </remarks>
public sealed class CtsSamRuleContext
{
    public CtsSamRuleContext(CtsSamRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
    }

    public CtsSamRecord Record { get; }

    public Cph Cph => Record.Cph;

    public bool ExistsInSam => Record.SamHoldingId is not null;

    /// <summary>
    /// Builds a finding seeded with the identity of the record, so that no rule has to remember
    /// which CTS fields every issue is expected to carry.
    /// </summary>
    public RuleFinding Issue(RuleDescriptor descriptor, Action<IDictionary<string, string>>? context = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ctsLocationNumber"] = Record.CtsLocationNumber
        };

        Add(values, "ctsPremisesType", Record.CtsPremisesType);
        Add(values, "ctsCountyName", Record.CtsCountyName);
        Add(values, "ctsKeeperSurname", Record.CtsKeeperSurname);
        Add(values, "ctsKeeperPostCode", Record.CtsKeeperPostCode);

        context?.Invoke(values);

        return new RuleFinding(descriptor, Cph, values);
    }

    private static void Add(IDictionary<string, string> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
        }
    }
}
