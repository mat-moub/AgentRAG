using Microsoft.AspNetCore.Identity;

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
    public string Title { get; set; } = default!;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<DocumentChunk> Chunks { get; set; } = [];
}

public class DocumentChunk
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public KnowledgeDocument? Document { get; set; }
    public int Index { get; set; }
    public string Content { get; set; } = default!;
    /// <summary>Embedding vector stored as raw float bytes.</summary>
    public byte[] Embedding { get; set; } = [];
}
