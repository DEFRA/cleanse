using Cleanse.Core.Domain;

namespace Cleanse.Rules.Domain;

/// <summary>
/// What a rule emits when it fires, before it has been turned into a Core detection.
/// </summary>
/// <remarks>
/// Rules build one of these rather than an <see cref="IssueDetection"/> directly so that the key
/// scheme and the descriptor-to-context mapping live in one place instead of being repeated,
/// and differently, in every rule.
/// </remarks>
public sealed record RuleFinding
{
    public RuleFinding(RuleDescriptor descriptor, Cph cph, IReadOnlyDictionary<string, string>? context = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!cph.HasValue)
        {
            throw new ArgumentException("A finding must carry a CPH.", nameof(cph));
        }

        Descriptor = descriptor;
        Cph = cph;
        Context = context ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public RuleDescriptor Descriptor { get; }

    public Cph Cph { get; }

    /// <summary>Rule-specific supporting data. The identity and descriptor keys are added later.</summary>
    public IReadOnlyDictionary<string, string> Context { get; }

    /// <summary>
    /// The natural key: the rule number and the CPH. Re-detecting the same anomaly on a later
    /// night therefore upserts onto the same issue, preserving its triage state and history.
    /// </summary>
    public IssueId IssueId => IssueId.Derive(Descriptor.RuleId, Cph.Value);
}
