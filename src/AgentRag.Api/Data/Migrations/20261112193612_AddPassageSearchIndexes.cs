using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace AgentRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPassageSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "Chunks",
                type: "vector(1536)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector");

            // Documents stored without indexing had no passages. Split their text the way RagService.Chunk does
            // (800 characters, 100 overlap) so they can be grepped and read by passage; they get no embedding.
            migrationBuilder.Sql("""
                INSERT INTO "Chunks" ("DocumentId", "Index", "Content")
                SELECT t."DocumentId", s.i, substr(t."Content", s.i * 700 + 1, 800)
                FROM "DocumentTexts" AS t
                CROSS JOIN LATERAL generate_series(0, GREATEST((length(t."Content") - 101) / 700, 0)) AS s(i)
                WHERE length(t."Content") > 0
                  AND NOT EXISTS (SELECT 1 FROM "Chunks" AS c WHERE c."DocumentId" = t."DocumentId");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_Content",
                table: "Chunks",
                column: "Content")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_Embedding",
                table: "Chunks",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chunks_Content",
                table: "Chunks");

            migrationBuilder.DropIndex(
                name: "IX_Chunks_Embedding",
                table: "Chunks");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            // Passages without embeddings only exist for documents stored without indexing.
            migrationBuilder.Sql("""DELETE FROM "Chunks" WHERE "Embedding" IS NULL;""");

            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "Chunks",
                type: "vector",
                nullable: false,
                oldClrType: typeof(Vector),
                oldType: "vector(1536)",
                oldNullable: true);
        }
    }
}
