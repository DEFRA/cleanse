using Cleanse.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cleanse.Storage.Postgres.Schema;

/// <summary>
/// The single description of the physical schema. EF generates migrations from this, and the
/// non-bulk read paths query through it, so the shape the migration creates is by construction
/// the shape the code expects.
/// </summary>
public sealed class CleanseDbContext(DbContextOptions<CleanseDbContext> options) : DbContext(options)
{
    internal DbSet<IssueRow> Issues => Set<IssueRow>();

    internal DbSet<RuleRunRow> RuleRuns => Set<RuleRunRow>();

    internal DbSet<AuditEntryRow> AuditEntries => Set<AuditEntryRow>();

    internal DbSet<IssueHistoryRow> IssueHistory => Set<IssueHistoryRow>();

    internal DbSet<ImportJobRow> ImportJobs => Set<ImportJobRow>();

    internal DbSet<ImportJobRejectionRow> ImportJobRejections => Set<ImportJobRejectionRow>();

    internal DbSet<DataResetRow> DataResets => Set<DataResetRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // CDP creates the database in 'public' with grants preconfigured; never emit CREATE SCHEMA.
        modelBuilder.HasDefaultSchema(null);

        ConfigureIssues(modelBuilder);
        ConfigureRuleRuns(modelBuilder);
        ConfigureAuditEntries(modelBuilder);
        ConfigureIssueHistory(modelBuilder);
        ConfigureImportJobs(modelBuilder);
        ConfigureDataResets(modelBuilder);
    }

    /// <summary>
    /// Deliberately outside the reset's blast radius: a table the wipe empties cannot answer
    /// "when was this last wiped".
    /// </summary>
    private static void ConfigureDataResets(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<DataResetRow>(entity =>
        {
            entity.ToTable(Db.DataResets);

            entity.HasKey(e => e.Sequence);
            entity.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(e => e.AtUtc).HasColumnName("at_utc");
            entity.Property(e => e.Scope).HasColumnName("scope");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.Issues).HasColumnName("issues");

            entity.HasIndex(e => e.AtUtc).IsDescending().HasDatabaseName("ix_data_resets_at");
        });

    private static void ConfigureIssues(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<IssueRow>(entity =>
        {
            entity.ToTable(Db.Issues, table =>
            {
                table.HasCheckConstraint(
                    "ck_issues_status",
                    $"{Db.Issue.Status} IN ('{StatusNames.Active}', '{StatusNames.Inactive}')");
                table.HasCheckConstraint(
                    "ck_issues_workflow_status",
                    $"{Db.Issue.WorkflowStatus} IN ('{StatusNames.Todo}', '{StatusNames.InProgress}', " +
                    $"'{StatusNames.Suppressed}', '{StatusNames.Resolved}', '{StatusNames.Ignored}', " +
                    $"'{StatusNames.Nonactionable}')");
                table.HasCheckConstraint(
                    "ck_issues_priority",
                    $"{Db.Issue.Priority} IS NULL OR {Db.Issue.Priority} IN " +
                    $"('{StatusNames.Low}', '{StatusNames.Medium}', '{StatusNames.High}')");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName(Db.Issue.Id);
            entity.Property(e => e.GroupId).HasColumnName(Db.Issue.GroupId);
            entity.Property(e => e.RuleId).HasColumnName(Db.Issue.RuleId);
            entity.Property(e => e.RunId).HasColumnName(Db.Issue.RunId);
            entity.Property(e => e.Status).HasColumnName(Db.Issue.Status);
            entity.Property(e => e.WorkflowStatus).HasColumnName(Db.Issue.WorkflowStatus);
            entity.Property(e => e.AssignedTo).HasColumnName(Db.Issue.AssignedTo);
            entity.Property(e => e.Notes).HasColumnName(Db.Issue.Notes);
            entity.Property(e => e.ExternalNotes).HasColumnName(Db.Issue.ExternalNotes);
            entity.Property(e => e.Priority).HasColumnName(Db.Issue.Priority);
            entity.Property(e => e.ExternalReceivedAtUtc).HasColumnName(Db.Issue.ExternalReceivedAtUtc);
            entity.Property(e => e.SuppressionExpiresAtUtc).HasColumnName(Db.Issue.SuppressionExpiresAtUtc);
            entity.Property(e => e.Context).HasColumnName(Db.Issue.Context).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAtUtc).HasColumnName(Db.Issue.CreatedAtUtc);
            entity.Property(e => e.UpdatedAtUtc).HasColumnName(Db.Issue.UpdatedAtUtc);
            entity.Property(e => e.ActivatedAtUtc).HasColumnName(Db.Issue.ActivatedAtUtc);
            entity.Property(e => e.DeactivatedAtUtc).HasColumnName(Db.Issue.DeactivatedAtUtc);
            entity.Property(e => e.Version).HasColumnName(Db.Issue.Version);

            // The end-of-run sweep: every active issue in the group the current run did not touch.
            entity.HasIndex(e => new { e.GroupId, e.Status, e.RunId }).HasDatabaseName("ix_issues_sweep");

            // Default search ordering. Id last so paging stays stable across equal timestamps.
            entity.HasIndex(e => new { e.GroupId, e.UpdatedAtUtc, e.Id })
                .HasDatabaseName("ix_issues_group_updated")
                .IsDescending(false, true, false);

            entity.HasIndex(e => new { e.GroupId, e.RuleId }).HasDatabaseName("ix_issues_group_rule");

            entity.HasIndex(e => e.AssignedTo)
                .HasDatabaseName("ix_issues_assigned")
                .HasFilter($"{Db.Issue.AssignedTo} IS NOT NULL");

            entity.HasIndex(e => e.SuppressionExpiresAtUtc)
                .HasDatabaseName("ix_issues_suppression_expiry")
                .HasFilter($"{Db.Issue.WorkflowStatus} = '{StatusNames.Suppressed}'");

            // jsonb_path_ops is smaller and faster than the default for pure containment (@>),
            // which is all the ContextMatches filter ever asks.
            entity.HasIndex(e => e.Context)
                .HasDatabaseName("ix_issues_context")
                .HasMethod("gin")
                .HasOperators("jsonb_path_ops");
        });

    private static void ConfigureRuleRuns(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<RuleRunRow>(entity =>
        {
            entity.ToTable(Db.RuleRuns, table => table.HasCheckConstraint(
                "ck_rule_runs_state",
                "state IN ('running', 'completed', 'abandoned', 'failed', 'cancelled')"));

            entity.HasKey(e => e.RunId);
            entity.Property(e => e.RunId).HasColumnName("run_id");
            entity.Property(e => e.GroupId).HasColumnName("group_id");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc");
            entity.Property(e => e.FinishedAtUtc).HasColumnName("finished_at_utc");
            entity.Property(e => e.AbandonReason).HasColumnName("abandon_reason");
            entity.Property(e => e.DetectionsProcessed).HasColumnName("detections_processed");
            entity.Property(e => e.IssuesCreated).HasColumnName("issues_created");
            entity.Property(e => e.IssuesReactivated).HasColumnName("issues_reactivated");
            entity.Property(e => e.IssuesUpdated).HasColumnName("issues_updated");
            entity.Property(e => e.IssuesUnchanged).HasColumnName("issues_unchanged");
            entity.Property(e => e.IssuesDeactivated).HasColumnName("issues_deactivated");
            entity.Property(e => e.CurrentStage).HasColumnName("current_stage");
            entity.Property(e => e.RecordsProcessed).HasColumnName("records_processed");
            entity.Property(e => e.RecordsTotal).HasColumnName("records_total");
            entity.Property(e => e.HeartbeatAtUtc).HasColumnName("heartbeat_at_utc");
            entity.Property(e => e.CancellationRequested).HasColumnName("cancellation_requested");
            entity.Property(e => e.FailureMessage).HasColumnName("failure_message");
            entity.Property(e => e.FailureDetail).HasColumnName("failure_detail");
            entity.Property(e => e.Version).HasColumnName("version");

            entity.HasIndex(e => new { e.GroupId, e.StartedAtUtc })
                .HasDatabaseName("ix_rule_runs_group_started")
                .IsDescending(false, true);

            entity.HasIndex(e => e.HeartbeatAtUtc)
                .HasDatabaseName("ix_rule_runs_heartbeat")
                .HasFilter("state = 'running'");

            // The domain checks this, but only the database can enforce it against two
            // schedulers racing to open a run for the same group.
            entity.HasIndex(e => e.GroupId)
                .HasDatabaseName("ux_rule_runs_one_running_per_group")
                .IsUnique()
                .HasFilter("state = 'running'");
        });

    private static void ConfigureAuditEntries(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<AuditEntryRow>(entity =>
        {
            // No foreign key to issues: this is an append-only log that must outlive what it
            // describes, and a constraint would add per-row cost to the bulk audit path.
            entity.ToTable(Db.AuditEntries);

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(e => e.IssueId).HasColumnName("issue_id");
            entity.Property(e => e.RunId).HasColumnName("run_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.ActorDisplayName).HasColumnName("actor_display_name");
            entity.Property(e => e.ActorKind).HasColumnName("actor_kind");
            entity.Property(e => e.AtUtc).HasColumnName("at_utc");
            entity.Property(e => e.Changes).HasColumnName("changes").HasColumnType("jsonb");

            entity.HasIndex(e => new { e.IssueId, e.AtUtc, e.Sequence })
                .HasDatabaseName("ix_audit_entries_issue")
                .IsDescending(false, true, true)
                .HasFilter("issue_id IS NOT NULL");

            entity.HasIndex(e => new { e.RunId, e.AtUtc, e.Sequence })
                .HasDatabaseName("ix_audit_entries_run")
                .IsDescending(false, true, true)
                .HasFilter("run_id IS NOT NULL");
        });

    private static void ConfigureIssueHistory(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<IssueHistoryRow>(entity =>
        {
            entity.ToTable(Db.IssueHistory, table => table.HasCheckConstraint(
                "ck_issue_history_action_type",
                "action_type IN ('Comment', 'LetterSent', 'PhoneCall')"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(e => e.IssueId).HasColumnName("issue_id");
            entity.Property(e => e.ActionType).HasColumnName("action_type");
            entity.Property(e => e.Comments).HasColumnName("comments");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.CreatedById).HasColumnName("created_by_id");
            entity.Property(e => e.CreatedByDisplayName).HasColumnName("created_by_display_name");
            entity.Property(e => e.CreatedByKind).HasColumnName("created_by_kind");

            entity.HasOne<IssueRow>()
                .WithMany()
                .HasForeignKey(e => e.IssueId)
                .HasConstraintName("fk_issue_history_issue")
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.IssueId, e.CreatedAtUtc, e.Sequence })
                .HasDatabaseName("ix_issue_history_issue")
                .IsDescending(false, true, true);
        });

    private static void ConfigureImportJobs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportJobRow>(entity =>
        {
            entity.ToTable(Db.ImportJobs, table => table.HasCheckConstraint(
                "ck_import_jobs_status",
                "status IN ('Pending', 'Processing', 'Completed', 'Failed', 'Abandoned')"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.GroupId).HasColumnName("group_id");
            entity.Property(e => e.FileName).HasColumnName("file_name");
            entity.Property(e => e.BlobKey).HasColumnName("blob_key");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.TotalRows).HasColumnName("total_rows");
            entity.Property(e => e.ProcessedRows).HasColumnName("processed_rows");
            entity.Property(e => e.Matched).HasColumnName("matched");
            entity.Property(e => e.Unmatched).HasColumnName("unmatched");
            entity.Property(e => e.DuplicatesCollapsed).HasColumnName("duplicates_collapsed");
            entity.Property(e => e.Invalid).HasColumnName("invalid");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc");
            entity.Property(e => e.FinishedAtUtc).HasColumnName("finished_at_utc");
            entity.Property(e => e.HeartbeatAtUtc).HasColumnName("heartbeat_at_utc");
            entity.Property(e => e.FailureReason).HasColumnName("failure_reason");
            entity.Property(e => e.Version).HasColumnName("version");

            // A double-click on upload cannot start two workers over the same group's issues.
            entity.HasIndex(e => e.GroupId)
                .HasDatabaseName("ux_import_jobs_one_active_per_group")
                .IsUnique()
                .HasFilter("status IN ('Pending', 'Processing')");

            entity.HasIndex(e => new { e.CreatedAtUtc, e.Id })
                .HasDatabaseName("ix_import_jobs_created")
                .IsDescending(true, true);

            entity.HasIndex(e => e.CreatedAtUtc)
                .HasDatabaseName("ix_import_jobs_pending")
                .HasFilter("status = 'Pending'");
        });

        modelBuilder.Entity<ImportJobRejectionRow>(entity =>
        {
            entity.ToTable(Db.ImportJobRejections);

            entity.HasKey(e => e.Sequence);
            entity.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(e => e.ImportJobId).HasColumnName("import_job_id");
            entity.Property(e => e.RowNumber).HasColumnName("row_number");
            entity.Property(e => e.IssueId).HasColumnName("issue_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.Detail).HasColumnName("detail");

            entity.HasOne<ImportJobRow>()
                .WithMany()
                .HasForeignKey(e => e.ImportJobId)
                .HasConstraintName("fk_import_job_rejections_job")
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.ImportJobId, e.RowNumber })
                .HasDatabaseName("ix_import_job_rejections_job");
        });
    }
}
