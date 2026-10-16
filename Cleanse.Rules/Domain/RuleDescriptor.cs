namespace Cleanse.Rules.Domain;

/// <summary>
/// The identity and reporting metadata of a rule.
/// </summary>
/// <remarks>
/// <see cref="RuleId"/> is the only part that becomes a natural key, so it is the only part that
/// cannot be changed once issues exist. Everything else is presentation: the stakeholders refer
/// to the same rule by its number, its error code and its error description interchangeably, and
/// all three need to survive onto the issue and out to a report.
/// </remarks>
public sealed record RuleDescriptor
{
    public RuleDescriptor(
        string ruleId,
        string ruleNumber,
        string name,
        string errorCode,
        string errorDescription,
        string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        RuleId = ruleId.Trim();
        RuleNumber = ruleNumber.Trim();
        Name = name.Trim();
        ErrorCode = errorCode.Trim();
        ErrorDescription = errorDescription.Trim();
        Summary = summary.Trim();
    }

    /// <summary>Stable key component. Changing this orphans every issue the rule has raised.</summary>
    public string RuleId { get; }

    public string RuleNumber { get; }

    public string Name { get; }

    public string ErrorCode { get; }

    public string ErrorDescription { get; }

    public string Summary { get; }
}

/// <summary>Whether a rule is evaluated. Rules are retired by switching them off, not deleting them.</summary>
public enum RuleStatus
{
    Active,
    OnHold
}
