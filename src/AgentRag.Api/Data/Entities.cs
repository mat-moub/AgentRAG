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
    /// <summary>False when the document's passages carry no embeddings: it can be read and grepped, not searched by similarity.</summary>
    public bool Indexed { get; set; } = true;
    /// <summary>Full extracted text; null for documents ingested before full text was stored.</summary>
    public DocumentText? Text { get; set; }
    public List<DocumentChunk> Chunks { get; set; } = [];
}

/// <summary>Full document text, kept in its own table so document listings never load it.</summary>
public class DocumentText
{
    public int DocumentId { get; set; }
    public KnowledgeDocument? Document { get; set; }
    public string Content { get; set; } = default!;
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
    /// <summary>Embedding stored as a native PostgreSQL vector; null for passages of documents that are not indexed.</summary>
    public Vector? Embedding { get; set; }
}
