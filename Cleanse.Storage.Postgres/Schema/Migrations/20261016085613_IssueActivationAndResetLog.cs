using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class IssueActivationAndResetLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added nullable, backfilled, then tightened. Adding it NOT NULL outright would need
            // a column default, and the only correct value is per row rather than constant.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "activated_at_utc",
                table: "issues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deactivated_at_utc",
                table: "issues",
                type: "timestamp with time zone",
                nullable: true);

            // An issue existing before this migration was active from the moment it was raised.
            migrationBuilder.Sql(
                "UPDATE issues SET activated_at_utc = created_at_utc WHERE activated_at_utc IS NULL;");

            // For one already swept inactive, the last write is the closest knowable moment.
            migrationBuilder.Sql(
                "UPDATE issues SET deactivated_at_utc = updated_at_utc " +
                "WHERE status = 'inactive' AND deactivated_at_utc IS NULL;");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "activated_at_utc",
                table: "issues",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "data_resets",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    issues = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_resets", x => x.sequence);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_resets_at",
                table: "data_resets",
                column: "at_utc",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_resets");

            migrationBuilder.DropColumn(
                name: "activated_at_utc",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "deactivated_at_utc",
                table: "issues");
        }
    }
}
