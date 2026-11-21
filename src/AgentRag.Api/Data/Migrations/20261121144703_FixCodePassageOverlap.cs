using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixCodePassageOverlap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "PassageOverlap",
                table: "Documents",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 100);

            // Repository code files are split on line ends without overlap, but the old default stored 100 for them.
            migrationBuilder.Sql("""UPDATE "Documents" SET "PassageOverlap" = 0 WHERE "RepositoryId" IS NOT NULL AND NOT "Indexed";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "PassageOverlap",
                table: "Documents",
                type: "integer",
                nullable: false,
                defaultValue: 100,
                oldClrType: typeof(int),
                oldType: "integer");
        }
    }
}
