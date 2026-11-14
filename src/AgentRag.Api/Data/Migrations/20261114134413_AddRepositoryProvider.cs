using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Project",
                table: "Repositories",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "Repositories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "github");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Project",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Repositories");
        }
    }
}
