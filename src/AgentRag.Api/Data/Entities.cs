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
    /// <summary>Set when the document is a file synchronised from a connected repository.</summary>
    public int? RepositoryId { get; set; }
    public RepositoryConnection? Repository { get; set; }
    /// <summary>Path of the file inside its repository. The title holds the same path, shortened if needed.</summary>
    public string? SourcePath { get; set; }
    /// <summary>Git blob SHA of the synchronised content; an unchanged SHA means the file is not fetched again.</summary>
    public string? SourceSha { get; set; }
    /// <summary>Characters repeated between consecutive passages: 100 for prose, 0 for files split on line ends.</summary>
    public int PassageOverlap { get; set; } = 100;
}

/// <summary>A repository on a Git host (GitHub, Azure DevOps) whose text files are mirrored as documents of one folder.</summary>
public class RepositoryConnection
{
    public int Id { get; set; }
    public int FolderId { get; set; }
    public KnowledgeFolder? Folder { get; set; }
    /// <summary>Git host, one of the <c>RepositoryAddress</c> provider names.</summary>
    public string Provider { get; set; } = "github";
    /// <summary>GitHub owner, or Azure DevOps organization.</summary>
    public string RepositoryOwner { get; set; } = default!;
    /// <summary>Azure DevOps project; null on GitHub.</summary>
    public string? Project { get; set; }
    public string RepositoryName { get; set; } = default!;
    public string Branch { get; set; } = default!;
    /// <summary>Access token encrypted with Data Protection. Never returned by the API or MCP tools.</summary>
    public string ProtectedToken { get; set; } = default!;
    public string? LastSyncedCommit { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
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
    /// <summary>One-based line of the file where this passage starts; set for files split on line ends.</summary>
    public int? StartLine { get; set; }
    /// <summary>Embedding stored as a native PostgreSQL vector; null for passages of documents that are not indexed.</summary>
    public Vector? Embedding { get; set; }
}
