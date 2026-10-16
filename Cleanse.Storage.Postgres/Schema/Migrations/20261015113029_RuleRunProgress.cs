using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class RuleRunProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_rule_runs_state",
                table: "rule_runs");

            migrationBuilder.AddColumn<bool>(
                name: "cancellation_requested",
                table: "rule_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "current_stage",
                table: "rule_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_detail",
                table: "rule_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_message",
                table: "rule_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "heartbeat_at_utc",
                table: "rule_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "records_processed",
                table: "rule_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "records_total",
                table: "rule_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_rule_runs_heartbeat",
                table: "rule_runs",
                column: "heartbeat_at_utc",
                filter: "state = 'running'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rule_runs_state",
                table: "rule_runs",
                sql: "state IN ('running', 'completed', 'abandoned', 'failed', 'cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_rule_runs_heartbeat",
                table: "rule_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rule_runs_state",
                table: "rule_runs");

            migrationBuilder.DropColumn(
                name: "cancellation_requested",
                table: "rule_runs");

            migrationBuilder.DropColumn(
                name: "current_stage",
                table: "rule_runs");

            migrationBuilder.DropColumn(
                name: "failure_detail",
                table: "rule_runs");

            migrationBuilder.DropColumn(
                name: "failure_message",
                table: "rule_runs");

            migrationBuilder.DropColumn(
                name: "heartbeat_at_utc",
                table: "rule_runs");

            migrationBuilder.DropColumn(
                name: "records_processed",
                table: "rule_runs");

            migrationBuilder.DropColumn(
                name: "records_total",
                table: "rule_runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rule_runs_state",
                table: "rule_runs",
                sql: "state IN ('running', 'completed', 'abandoned')");
        }
    }
}
