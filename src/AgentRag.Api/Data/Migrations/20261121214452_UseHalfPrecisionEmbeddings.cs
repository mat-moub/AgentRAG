using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRag.Api.Data.Migrations
{
    /// <summary>
    /// Stores embeddings as halfvec(512) instead of vector(1536), about a sixth of the space for the vectors and their
    /// HNSW indexes. text-embedding-3 vectors are trained so that their first values carry most of the meaning: keeping
    /// the first 512 and normalising them gives what the model returns when asked for 512 dimensions, so nothing is
    /// sent to the model again. Requires pgvector 0.7 or later (halfvec, subvector, l2_normalize).
    /// </summary>
    public partial class UseHalfPrecisionEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WikiPages_Embedding",
                table: "WikiPages");

            migrationBuilder.DropIndex(
                name: "IX_Chunks_Embedding",
                table: "Chunks");

            // Rewriting the column also rewrites the tables, which leaves out the space of deleted rows.
            migrationBuilder.Sql("""
                ALTER TABLE "Chunks" ALTER COLUMN "Embedding" TYPE halfvec(512)
                    USING l2_normalize(subvector("Embedding", 1, 512))::halfvec(512);
                """);
            migrationBuilder.Sql("""
                ALTER TABLE "WikiPages" ALTER COLUMN "Embedding" TYPE halfvec(512)
                    USING l2_normalize(subvector("Embedding", 1, 512))::halfvec(512);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_Embedding",
                table: "WikiPages",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "halfvec_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_Embedding",
                table: "Chunks",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "halfvec_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WikiPages_Embedding",
                table: "WikiPages");

            migrationBuilder.DropIndex(
                name: "IX_Chunks_Embedding",
                table: "Chunks");

            // The 1,024 dropped values cannot be rebuilt: embeddings are cleared, and documents must be indexed again
            // (wiki pages get theirs back at the next refresh).
            migrationBuilder.Sql("""ALTER TABLE "Chunks" ALTER COLUMN "Embedding" TYPE vector(1536) USING NULL;""");
            migrationBuilder.Sql("""ALTER TABLE "WikiPages" ALTER COLUMN "Embedding" TYPE vector(1536) USING NULL;""");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_Embedding",
                table: "WikiPages",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_Embedding",
                table: "Chunks",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }
    }
}
