using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRag.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AgentRag.Api.Services;

public sealed class LibraryException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class LibraryExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not LibraryException error) return;
        context.Result = new ObjectResult(new ProblemDetails { Status = error.Status, Detail = error.Message }) { StatusCode = error.Status };
        context.ExceptionHandled = true;
    }
}

/// <summary>One authorization policy for HTTP, ingestion, retrieval and MCP. Snapshots are never cached across requests.</summary>
public sealed class LibraryAccess(AppDbContext db)
{
    public async Task<LibrarySnapshot> LoadAsync(string userId, CancellationToken ct = default)
    {
        // Only owners relevant to this user are loaded. Private ancestors are used internally,
        // but are never included in the visible tree or its breadcrumbs.
        var sharedOwners = db.FolderMembers.Where(m => m.UserId == userId).Select(m => m.Folder.OwnerId);
        var folders = await db.Folders.AsNoTracking().Where(f => f.OwnerId == userId || sharedOwners.Contains(f.OwnerId)).ToListAsync(ct);
        var ids = folders.Select(f => f.Id).ToArray();
        var members = await db.FolderMembers.AsNoTracking().Where(m => ids.Contains(m.FolderId)).ToListAsync(ct);
        return new(userId, folders, members);
    }

    public async Task<IQueryable<KnowledgeDocument>> ReadableDocumentsAsync(string userId, int? folderId = null, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(userId, ct);
        return snapshot.Documents(db, folderId);
    }

    public async Task<string> RevisionAsync(LibrarySnapshot snapshot, CancellationToken ct)
    {
        var documents = await snapshot.Documents(db).OrderBy(d => d.Id).Select(d => new { d.Id, d.FolderId }).ToListAsync(ct);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            folders = snapshot.Folders.Values.Where(f => snapshot.Role(f.Id) is not null).OrderBy(f => f.Id)
                .Select(f => new { f.Id, f.ParentId, Role = snapshot.Role(f.Id), ShareRootId = snapshot.ShareRoot(f.Id) }),
            documents
        }))));
    }

    public static async Task<IDbContextTransaction?> BeginMutationAsync(AppDbContext db, CancellationToken ct) =>
        db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
}

public sealed class LibrarySnapshot(string userId, IReadOnlyList<KnowledgeFolder> folders, IReadOnlyList<FolderMember> members)
{
    public string UserId { get; } = userId;
    public IReadOnlyDictionary<int, KnowledgeFolder> Folders { get; } = folders.ToDictionary(f => f.Id);
    public IReadOnlyList<FolderMember> Members { get; } = members;

    public int? ShareRoot(int id)
    {
        var visited = new HashSet<int>();
        while (Folders.TryGetValue(id, out var folder) && visited.Add(id))
        {
            if (Members.Any(m => m.FolderId == id)) return id;
            if (folder.ParentId is not { } parent) return null;
            id = parent;
        }
        return null;
    }

    public string? Role(int id)
    {
        if (!Folders.TryGetValue(id, out var folder)) return null;
        if (folder.OwnerId == UserId) return "owner";
        return ShareRoot(id) is { } root ? Members.FirstOrDefault(m => m.FolderId == root && m.UserId == UserId)?.Role : null;
    }

    public bool CanWrite(int id) => Role(id) is "owner" or "write";
    public bool CanEditFolder(int id) => Role(id) == "owner" || (Role(id) == "write" && ShareRoot(id) != id);

    public KnowledgeFolder Require(int id, bool write = false, bool owner = false, bool edit = false)
    {
        if (Role(id) is null) throw new LibraryException(404, "This item is unavailable or you no longer have access.");
        if ((owner && Role(id) != "owner") || (write && !CanWrite(id)) || (edit && !CanEditFolder(id)))
            throw new LibraryException(403, "Your access does not allow this action.");
        return Folders[id];
    }

    public HashSet<int> Descendants(int id)
    {
        var result = new HashSet<int> { id };
        var queue = new Queue<int>();
        queue.Enqueue(id);
        while (queue.TryDequeue(out var parent))
            foreach (var child in Folders.Values.Where(f => f.ParentId == parent))
                if (result.Add(child.Id)) queue.Enqueue(child.Id);
        return result;
    }

    public IQueryable<KnowledgeDocument> Documents(AppDbContext db, int? folderId = null)
    {
        var readable = Folders.Keys.Where(id => Role(id) is not null).ToArray();
        var query = db.Documents.AsNoTracking().Where(d => d.FolderId == null ? d.OwnerId == UserId : readable.Contains(d.FolderId.Value));
        if (folderId is { } id)
        {
            Require(id);
            var descendants = Descendants(id).ToArray();
            query = query.Where(d => d.FolderId != null && descendants.Contains(d.FolderId.Value));
        }
        return query;
    }

    public bool CanWriteDocument(KnowledgeDocument document) => document.FolderId is { } id ? CanWrite(id) : document.OwnerId == UserId;

    public string Path(int id)
    {
        var parts = new List<string>();
        var visited = new HashSet<int>();
        while (Folders.TryGetValue(id, out var folder) && Role(id) is not null && visited.Add(id))
        {
            parts.Add(folder.Name);
            if (folder.ParentId is not { } parent) break;
            id = parent;
        }
        parts.Reverse();
        return string.Join(" / ", parts);
    }
}
