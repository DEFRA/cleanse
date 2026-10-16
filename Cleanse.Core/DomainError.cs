namespace Cleanse.Core;

/// <summary>An expected, non-exceptional failure. Codes are stable so a host can map them.</summary>
public sealed record DomainError(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

public static class ErrorCodes
{
    public const string IssueNotFound = "issue.not_found";
    public const string IssueGroupMismatch = "issue.group_mismatch";
    public const string IssueInvalidTransition = "issue.invalid_transition";
    public const string IssueNotSuppressed = "issue.not_suppressed";
    public const string IssueSuppressionExpiryInPast = "issue.suppression_expiry_in_past";
    public const string IssueConcurrencyConflict = "issue.concurrency_conflict";

    public const string RunNotFound = "run.not_found";
    public const string RunAlreadyInProgress = "run.already_in_progress";
    public const string RunNotRunning = "run.not_running";
    public const string RulesEngineDisabled = "rules.engine_disabled";

    public const string ImportNotFound = "import.not_found";
    public const string ImportAlreadyInProgress = "import.already_in_progress";
    public const string ImportNotPending = "import.not_pending";
    public const string ImportNotProcessing = "import.not_processing";
    public const string ImportAlreadyFinished = "import.already_finished";
    public const string ImportFileInvalid = "import.file_invalid";

    public const string ValidationFailed = "validation.failed";
}

public static class DomainErrors
{
    public static DomainError IssueNotFound(string id) =>
        new(ErrorCodes.IssueNotFound, $"Issue '{id}' was not found.");

    public static DomainError IssueGroupMismatch(string id, string expectedGroupId, string actualGroupId) =>
        new(ErrorCodes.IssueGroupMismatch,
            $"Issue '{id}' belongs to group '{actualGroupId}' but the run is for group '{expectedGroupId}'.");

    public static DomainError InvalidTransition(string id, string from, string to) =>
        new(ErrorCodes.IssueInvalidTransition,
            $"Issue '{id}' cannot move from workflow status '{from}' to '{to}'.");

    public static DomainError NotSuppressed(string id) =>
        new(ErrorCodes.IssueNotSuppressed, $"Issue '{id}' is not suppressed.");

    public static DomainError SuppressionExpiryInPast(string id) =>
        new(ErrorCodes.IssueSuppressionExpiryInPast,
            $"The suppression expiry for issue '{id}' must be in the future.");

    public static DomainError ConcurrencyConflict(string id, int expectedVersion, int actualVersion) =>
        new(ErrorCodes.IssueConcurrencyConflict,
            $"Issue '{id}' has version {actualVersion}, not the expected {expectedVersion}.");

    public static DomainError RunNotFound(string runId) =>
        new(ErrorCodes.RunNotFound, $"Run '{runId}' was not found.");

    public static DomainError RunAlreadyInProgress(string groupId, string runId) =>
        new(ErrorCodes.RunAlreadyInProgress,
            $"Group '{groupId}' already has run '{runId}' in progress.");

    public static DomainError RunNotRunning(string runId, string state) =>
        new(ErrorCodes.RunNotRunning, $"Run '{runId}' is '{state}', not running.");

    public static DomainError RulesEngineDisabled() =>
        new(ErrorCodes.RulesEngineDisabled,
            "The rules engine is disabled in this environment. Set Rules:Enabled to start runs.");

    public static DomainError Validation(string message) =>
        new(ErrorCodes.ValidationFailed, message);

    public static DomainError ImportNotFound(Guid id) =>
        new(ErrorCodes.ImportNotFound, $"Import '{id}' was not found.");

    public static DomainError ImportAlreadyInProgress(string groupId, Guid id) =>
        new(ErrorCodes.ImportAlreadyInProgress,
            $"Group '{groupId}' already has import '{id}' in progress.");

    public static DomainError ImportNotPending(Guid id, string status) =>
        new(ErrorCodes.ImportNotPending, $"Import '{id}' is '{status}', not pending.");

    public static DomainError ImportNotProcessing(Guid id, string status) =>
        new(ErrorCodes.ImportNotProcessing, $"Import '{id}' is '{status}', not processing.");

    public static DomainError ImportAlreadyFinished(Guid id, string status) =>
        new(ErrorCodes.ImportAlreadyFinished, $"Import '{id}' has already finished as '{status}'.");

    public static DomainError ImportFileInvalid(string message) =>
        new(ErrorCodes.ImportFileInvalid, message);
}
