using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class ImportAssignmentScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_id",
                table: "import_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                table: "import_jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "client_id",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "import_jobs");
        }
    }
}
