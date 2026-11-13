using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgentRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PassageOverlap",
                table: "Documents",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<int>(
                name: "RepositoryId",
                table: "Documents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePath",
                table: "Documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSha",
                table: "Documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StartLine",
                table: "Chunks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FolderId = table.Column<int>(type: "integer", nullable: false),
                    RepositoryOwner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RepositoryName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProtectedToken = table.Column<string>(type: "text", nullable: false),
                    LastSyncedCommit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSyncedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Repositories_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_RepositoryId",
                table: "Documents",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_FolderId",
                table: "Repositories",
                column: "FolderId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_Repositories_RepositoryId",
                table: "Documents",
                column: "RepositoryId",
                principalTable: "Repositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Documents_Repositories_RepositoryId",
                table: "Documents");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_Documents_RepositoryId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "PassageOverlap",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "RepositoryId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "SourcePath",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "SourceSha",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "StartLine",
                table: "Chunks");
        }
    }
}
