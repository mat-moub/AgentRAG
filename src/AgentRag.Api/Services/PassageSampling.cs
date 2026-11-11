using AgentRag.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentRag.Api.Services;

public static class PassageSampling
{
    public const int Limit = 500;

    public static IQueryable<int> SelectAccessibleIds(AppDbContext db, int[] documentIds) =>
        db.Database.SqlQuery<int>($"""
            SELECT c."Id" AS "Value"
            FROM "Chunks" AS c
            WHERE c."DocumentId" = ANY({documentIds})
            ORDER BY ROW_NUMBER() OVER (PARTITION BY c."DocumentId" ORDER BY c."Index", c."Id"), c."DocumentId"
            LIMIT {Limit}
            """);

    // Rank within each document first so large documents cannot fill the entire sample.
    // Rank only IDs here; embeddings are fetched after the bounded sample is chosen.
    public static IQueryable<int> SelectIds(AppDbContext db, string ownerId, int? documentId) =>
        db.Database.SqlQuery<int>($"""
            SELECT c."Id" AS "Value"
            FROM "Chunks" AS c
            INNER JOIN "Documents" AS d ON d."Id" = c."DocumentId"
            WHERE d."OwnerId" = {ownerId}
              AND (CAST({documentId} AS integer) IS NULL OR c."DocumentId" = {documentId})
            ORDER BY ROW_NUMBER() OVER (PARTITION BY c."DocumentId" ORDER BY c."Index", c."Id"), c."DocumentId"
            LIMIT {Limit}
            """);
}
