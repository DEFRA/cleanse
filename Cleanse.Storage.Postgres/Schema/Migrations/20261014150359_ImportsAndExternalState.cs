using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class ImportsAndExternalState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_issues_workflow_status",
                table: "issues");

            migrationBuilder.AddColumn<string>(
                name: "external_notes",
                table: "issues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "external_received_at_utc",
                table: "issues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "priority",
                table: "issues",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "import_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    blob_key = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: false),
                    processed_rows = table.Column<int>(type: "integer", nullable: false),
                    matched = table.Column<int>(type: "integer", nullable: false),
                    unmatched = table.Column<int>(type: "integer", nullable: false),
                    duplicates_collapsed = table.Column<int>(type: "integer", nullable: false),
                    invalid = table.Column<int>(type: "integer", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    heartbeat_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_jobs", x => x.id);
                    table.CheckConstraint("ck_import_jobs_status", "status IN ('Pending', 'Processing', 'Completed', 'Failed', 'Abandoned')");
                });

            migrationBuilder.CreateTable(
                name: "import_job_rejections",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<long>(type: "bigint", nullable: false),
                    issue_id = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_job_rejections", x => x.sequence);
                    table.ForeignKey(
                        name: "fk_import_job_rejections_job",
                        column: x => x.import_job_id,
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_issues_priority",
                table: "issues",
                sql: "priority IS NULL OR priority IN ('low', 'medium', 'high')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_issues_workflow_status",
                table: "issues",
                sql: "workflow_status IN ('todo', 'in-progress', 'suppressed', 'resolved', 'ignored', 'nonactionable')");

            migrationBuilder.CreateIndex(
                name: "ix_import_job_rejections_job",
                table: "import_job_rejections",
                columns: new[] { "import_job_id", "row_number" });

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_created",
                table: "import_jobs",
                columns: new[] { "created_at_utc", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_pending",
                table: "import_jobs",
                column: "created_at_utc",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_import_jobs_one_active_per_group",
                table: "import_jobs",
                column: "group_id",
                unique: true,
                filter: "status IN ('Pending', 'Processing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_job_rejections");

            migrationBuilder.DropTable(
                name: "import_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_issues_priority",
                table: "issues");

            migrationBuilder.DropCheckConstraint(
                name: "ck_issues_workflow_status",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "external_notes",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "external_received_at_utc",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "priority",
                table: "issues");

            migrationBuilder.AddCheckConstraint(
                name: "ck_issues_workflow_status",
                table: "issues",
                sql: "workflow_status IN ('todo', 'in-progress', 'suppressed', 'resolved', 'ignored')");
        }
    }
}
