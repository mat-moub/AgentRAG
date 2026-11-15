using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace AgentRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWiki : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Wikis",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FolderId = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Guidelines = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wikis", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Wikis_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WikiPages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WikiId = table.Column<int>(type: "integer", nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Aliases = table.Column<string[]>(type: "text[]", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(1536)", nullable: true),
                    Stale = table.Column<bool>(type: "boolean", nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiPages_Wikis_WikiId",
                        column: x => x.WikiId,
                        principalTable: "Wikis",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WikiRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WikiId = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Stage = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Completed = table.Column<int>(type: "integer", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    RequestedById = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Stats = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiRuns_Wikis_WikiId",
                        column: x => x.WikiId,
                        principalTable: "Wikis",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WikiSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WikiId = table.Column<int>(type: "integer", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false),
                    DocumentId = table.Column<int>(type: "integer", nullable: true),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Extraction = table.Column<string>(type: "jsonb", nullable: true),
                    ProcessedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiSources_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WikiSources_Wikis_WikiId",
                        column: x => x.WikiId,
                        principalTable: "Wikis",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WikiLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WikiId = table.Column<int>(type: "integer", nullable: false),
                    FromPageId = table.Column<int>(type: "integer", nullable: false),
                    ToPageId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiLinks_WikiPages_FromPageId",
                        column: x => x.FromPageId,
                        principalTable: "WikiPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WikiLinks_WikiPages_ToPageId",
                        column: x => x.ToPageId,
                        principalTable: "WikiPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WikiPageSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PageId = table.Column<int>(type: "integer", nullable: false),
                    DocumentId = table.Column<int>(type: "integer", nullable: true),
                    SourceKey = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiPageSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiPageSources_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WikiPageSources_WikiPages_PageId",
                        column: x => x.PageId,
                        principalTable: "WikiPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WikiLinks_FromPageId_ToPageId_Kind",
                table: "WikiLinks",
                columns: new[] { "FromPageId", "ToPageId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiLinks_ToPageId",
                table: "WikiLinks",
                column: "ToPageId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiLinks_WikiId",
                table: "WikiLinks",
                column: "WikiId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_Content",
                table: "WikiPages",
                column: "Content")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_Embedding",
                table: "WikiPages",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_WikiId_Slug",
                table: "WikiPages",
                columns: new[] { "WikiId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiPageSources_DocumentId",
                table: "WikiPageSources",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPageSources_PageId",
                table: "WikiPageSources",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiRuns_Active",
                table: "WikiRuns",
                column: "WikiId",
                unique: true,
                filter: "\"Status\" IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "IX_WikiRuns_WikiId_Id",
                table: "WikiRuns",
                columns: new[] { "WikiId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Wikis_FolderId",
                table: "Wikis",
                column: "FolderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiSources_DocumentId",
                table: "WikiSources",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiSources_WikiId_SourceKey",
                table: "WikiSources",
                columns: new[] { "WikiId", "SourceKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WikiLinks");

            migrationBuilder.DropTable(
                name: "WikiPageSources");

            migrationBuilder.DropTable(
                name: "WikiRuns");

            migrationBuilder.DropTable(
                name: "WikiSources");

            migrationBuilder.DropTable(
                name: "WikiPages");

            migrationBuilder.DropTable(
                name: "Wikis");
        }
    }
}
