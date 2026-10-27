using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class KnownUsersAndAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "assigned_user_full_name",
                table: "issues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "assigned_user_id",
                table: "issues",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    full_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    first_logged_in_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_logged_in_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_issues_assigned_user_id",
                table: "issues",
                column: "assigned_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_scope",
                table: "users",
                columns: new[] { "tenant_id", "client_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_users_identity",
                table: "users",
                columns: new[] { "issuer", "client_id", "subject" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_issues_users_assigned_user_id",
                table: "issues",
                column: "assigned_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_issues_users_assigned_user_id",
                table: "issues");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropIndex(
                name: "IX_issues_assigned_user_id",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "assigned_user_full_name",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "assigned_user_id",
                table: "issues");
        }
    }
}
