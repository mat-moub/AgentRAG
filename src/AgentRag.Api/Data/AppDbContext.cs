using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AgentRag.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options), IDataProtectionKeyContext
{
    /// <summary>Vector size of text-embedding-3-small, the default embedding model.</summary>
    public const int EmbeddingDimensions = 1536;

    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();
    public DbSet<DocumentChunk> Chunks => Set<DocumentChunk>();
    public DbSet<DocumentText> DocumentTexts => Set<DocumentText>();
    public DbSet<KnowledgeFolder> Folders => Set<KnowledgeFolder>();
    public DbSet<FolderMember> FolderMembers => Set<FolderMember>();
    public DbSet<RepositoryConnection> Repositories => Set<RepositoryConnection>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
        builder.HasPostgresExtension("vector");
        builder.HasPostgresExtension("pg_trgm");
        builder.Entity<DocumentChunk>(e =>
        {
            // HNSW needs a fixed dimension; it must match the configured embedding model.
            e.Property(c => c.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
            e.HasIndex(c => c.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
            // Trigram index: serves both ILIKE and regular-expression passage searches.
            e.HasIndex(c => c.Content).HasMethod("gin").HasOperators("gin_trgm_ops");
        });
        builder.Entity<KnowledgeDocument>(e =>
        {
            e.Property(d => d.Title).HasMaxLength(256);
            e.HasOne(d => d.Owner).WithMany().HasForeignKey(d => d.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(d => d.Chunks).WithOne(c => c.Document).HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Folder).WithMany().HasForeignKey(d => d.FolderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Text).WithOne(t => t.Document).HasForeignKey<DocumentText>(t => t.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<RepositoryConnection>(e =>
        {
            e.ToTable("Repositories");
            e.Property(r => r.Provider).HasMaxLength(20).HasDefaultValue("github");
            e.Property(r => r.Project).HasMaxLength(100);
            e.Property(r => r.RepositoryOwner).HasMaxLength(100);
            e.Property(r => r.RepositoryName).HasMaxLength(100);
            e.Property(r => r.Branch).HasMaxLength(255);
            e.Property(r => r.LastSyncedCommit).HasMaxLength(64);
            e.Property(r => r.LastError).HasMaxLength(500);
            // One repository per folder. Deleting the folder removes the connection and its stored token.
            e.HasIndex(r => r.FolderId).IsUnique();
            e.HasOne(r => r.Folder).WithMany().HasForeignKey(r => r.FolderId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<KnowledgeDocument>(e =>
        {
            e.Property(d => d.SourceSha).HasMaxLength(64);
            e.Property(d => d.PassageOverlap).HasDefaultValue(100);
            e.HasOne(d => d.Repository).WithMany().HasForeignKey(d => d.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<DocumentText>(e =>
        {
            e.ToTable("DocumentTexts");
            e.HasKey(t => t.DocumentId);
        });
        builder.Entity<KnowledgeFolder>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(256);
            e.HasOne(f => f.Owner).WithMany().HasForeignKey(f => f.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(f => f.Parent).WithMany().HasForeignKey(f => f.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<FolderMember>(e =>
        {
            e.HasKey(m => new { m.FolderId, m.UserId });
            e.Property(m => m.Role).HasMaxLength(5);
            e.ToTable(t => t.HasCheckConstraint("CK_FolderMembers_Role", "\"Role\" IN ('read', 'write')"));
            e.HasOne(m => m.Folder).WithMany().HasForeignKey(m => m.FolderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
