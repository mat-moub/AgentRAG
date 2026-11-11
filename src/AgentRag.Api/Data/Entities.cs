using Microsoft.AspNetCore.Identity;
using Pgvector;

namespace AgentRag.Api.Data;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
}

public class KnowledgeDocument
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = default!;
    public ApplicationUser? Owner { get; set; }
    public int? FolderId { get; set; }
    public KnowledgeFolder? Folder { get; set; }
    public string? AddedById { get; set; }
    public string Title { get; set; } = default!;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<DocumentChunk> Chunks { get; set; } = [];
}

public class KnowledgeFolder
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string OwnerId { get; set; } = default!;
    public ApplicationUser? Owner { get; set; }
    public int? ParentId { get; set; }
    public KnowledgeFolder? Parent { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class FolderMember
{
    public int FolderId { get; set; }
    public KnowledgeFolder Folder { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public ApplicationUser User { get; set; } = default!;
    public string Role { get; set; } = "read";
}

public class DocumentChunk
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public KnowledgeDocument? Document { get; set; }
    public int Index { get; set; }
    public string Content { get; set; } = default!;
    /// <summary>Embedding stored as a native PostgreSQL vector.</summary>
    public Vector Embedding { get; set; } = default!;
}
