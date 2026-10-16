namespace Cleanse.Rules.Engine;

/// <summary>How a run ended, and what it did.</summary>
public sealed record RuleEngineResult(
    string RunId,
    RuleEngineOutcome Outcome,
    int RecordsScanned,
    int FindingsRaised,
    int IssuesCreated,
    int IssuesReactivated,
    int IssuesDeactivated,
    string? FailureMessage);

public enum RuleEngineOutcome
{
    Completed,
    Cancelled,
    Failed
}
