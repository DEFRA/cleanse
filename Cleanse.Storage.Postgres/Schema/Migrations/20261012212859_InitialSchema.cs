using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    issue_id = table.Column<string>(type: "text", nullable: true),
                    run_id = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    actor_display_name = table.Column<string>(type: "text", nullable: false),
                    actor_kind = table.Column<string>(type: "text", nullable: false),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "issues",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    group_id = table.Column<string>(type: "text", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    workflow_status = table.Column<string>(type: "text", nullable: false),
                    assigned_to = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    suppression_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    context = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issues", x => x.id);
                    table.CheckConstraint("ck_issues_status", "status IN ('active', 'inactive')");
                    table.CheckConstraint("ck_issues_workflow_status", "workflow_status IN ('todo', 'in-progress', 'suppressed', 'resolved', 'ignored')");
                });

            migrationBuilder.CreateTable(
                name: "rule_runs",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "text", nullable: false),
                    group_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    abandon_reason = table.Column<string>(type: "text", nullable: true),
                    detections_processed = table.Column<int>(type: "integer", nullable: false),
                    issues_created = table.Column<int>(type: "integer", nullable: false),
                    issues_reactivated = table.Column<int>(type: "integer", nullable: false),
                    issues_updated = table.Column<int>(type: "integer", nullable: false),
                    issues_unchanged = table.Column<int>(type: "integer", nullable: false),
                    issues_deactivated = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rule_runs", x => x.run_id);
                    table.CheckConstraint("ck_rule_runs_state", "state IN ('running', 'completed', 'abandoned')");
                });

            migrationBuilder.CreateTable(
                name: "issue_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    issue_id = table.Column<string>(type: "text", nullable: false),
                    action_type = table.Column<string>(type: "text", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<string>(type: "text", nullable: false),
                    created_by_display_name = table.Column<string>(type: "text", nullable: false),
                    created_by_kind = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issue_history", x => x.id);
                    table.CheckConstraint("ck_issue_history_action_type", "action_type IN ('Comment', 'LetterSent', 'PhoneCall')");
                    table.ForeignKey(
                        name: "fk_issue_history_issue",
                        column: x => x.issue_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_issue",
                table: "audit_entries",
                columns: new[] { "issue_id", "at_utc", "sequence" },
                descending: new[] { false, true, true },
                filter: "issue_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_run",
                table: "audit_entries",
                columns: new[] { "run_id", "at_utc", "sequence" },
                descending: new[] { false, true, true },
                filter: "run_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_issue_history_issue",
                table: "issue_history",
                columns: new[] { "issue_id", "created_at_utc", "sequence" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_issues_assigned",
                table: "issues",
                column: "assigned_to",
                filter: "assigned_to IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_issues_context",
                table: "issues",
                column: "context")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_group_rule",
                table: "issues",
                columns: new[] { "group_id", "rule_id" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_group_updated",
                table: "issues",
                columns: new[] { "group_id", "updated_at_utc", "id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "ix_issues_suppression_expiry",
                table: "issues",
                column: "suppression_expires_at_utc",
                filter: "workflow_status = 'suppressed'");

            migrationBuilder.CreateIndex(
                name: "ix_issues_sweep",
                table: "issues",
                columns: new[] { "group_id", "status", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_rule_runs_group_started",
                table: "rule_runs",
                columns: new[] { "group_id", "started_at_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_rule_runs_one_running_per_group",
                table: "rule_runs",
                column: "group_id",
                unique: true,
                filter: "state = 'running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "issue_history");

            migrationBuilder.DropTable(
                name: "rule_runs");

            migrationBuilder.DropTable(
                name: "issues");
        }
    }
}
