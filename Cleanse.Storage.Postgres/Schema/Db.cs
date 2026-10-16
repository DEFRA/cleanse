namespace Cleanse.Storage.Postgres.Schema;

/// <summary>
/// Physical names, spelled once. The raw-SQL paths and the EF configuration both read from here,
/// so a rename cannot leave one of them behind.
/// </summary>
internal static class Db
{
    public const string Issues = "issues";
    public const string RuleRuns = "rule_runs";
    public const string AuditEntries = "audit_entries";
    public const string IssueHistory = "issue_history";
    public const string ImportJobs = "import_jobs";
    public const string ImportJobRejections = "import_job_rejections";
    public const string DataResets = "data_resets";

    public static class Issue
    {
        public const string Id = "id";
        public const string GroupId = "group_id";
        public const string RuleId = "rule_id";
        public const string RunId = "run_id";
        public const string Status = "status";
        public const string WorkflowStatus = "workflow_status";
        public const string AssignedTo = "assigned_to";
        public const string Notes = "notes";
        public const string ExternalNotes = "external_notes";
        public const string Priority = "priority";
        public const string ExternalReceivedAtUtc = "external_received_at_utc";
        public const string SuppressionExpiresAtUtc = "suppression_expires_at_utc";
        public const string Context = "context";
        public const string CreatedAtUtc = "created_at_utc";
        public const string UpdatedAtUtc = "updated_at_utc";
        public const string ActivatedAtUtc = "activated_at_utc";
        public const string DeactivatedAtUtc = "deactivated_at_utc";
        public const string Version = "version";

        /// <summary>Insert order. The parameter binding is built from the same order.</summary>
        public static readonly string[] All =
        [
            Id, GroupId, RuleId, RunId, Status, WorkflowStatus, AssignedTo, Notes,
            ExternalNotes, Priority, ExternalReceivedAtUtc, SuppressionExpiresAtUtc,
            Context, CreatedAtUtc, UpdatedAtUtc, ActivatedAtUtc, DeactivatedAtUtc, Version
        ];

        public static readonly string AllColumns = string.Join(", ", All);
    }
}
