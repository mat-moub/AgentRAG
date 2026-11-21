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
    public DbSet<Wiki> Wikis => Set<Wiki>();
    public DbSet<WikiPage> WikiPages => Set<WikiPage>();
    public DbSet<WikiLink> WikiLinks => Set<WikiLink>();
    public DbSet<WikiPageSource> WikiPageSources => Set<WikiPageSource>();
    public DbSet<WikiSource> WikiSources => Set<WikiSource>();
    public DbSet<WikiRun> WikiRuns => Set<WikiRun>();

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
            // No database default: with one, EF Core leaves out the CLR default 0 of a code file and PostgreSQL stores 100,
            // which makes read_passages cut 100 characters at every seam of line-split passages.
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
        builder.Entity<Wiki>(e =>
        {
            e.Property(w => w.Language).HasMaxLength(20);
            e.Property(w => w.Guidelines).HasMaxLength(8000);
            // One wiki per folder. Deleting the folder removes its wiki.
            e.HasIndex(w => w.FolderId).IsUnique();
            e.HasOne(w => w.Folder).WithMany().HasForeignKey(w => w.FolderId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<WikiPage>(e =>
        {
            e.Property(p => p.Slug).HasMaxLength(120);
            e.Property(p => p.Title).HasMaxLength(256);
            e.Property(p => p.Kind).HasMaxLength(20);
            e.Property(p => p.Summary).HasMaxLength(1000);
            e.Property(p => p.InputHash).HasMaxLength(64);
            e.HasIndex(p => new { p.WikiId, p.Slug }).IsUnique();
            e.HasOne(p => p.Wiki).WithMany().HasForeignKey(p => p.WikiId).OnDelete(DeleteBehavior.Cascade);
            // The in-memory provider used by the tests has no vector type.
            if (Database.IsRelational())
            {
                e.Property(p => p.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
                e.HasIndex(p => p.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
                e.HasIndex(p => p.Content).HasMethod("gin").HasOperators("gin_trgm_ops");
            }
            else
            {
                e.Ignore(p => p.Embedding);
            }
        });
        builder.Entity<WikiLink>(e =>
        {
            e.Property(l => l.Kind).HasMaxLength(20);
            e.Property(l => l.Note).HasMaxLength(1000);
            e.HasIndex(l => new { l.FromPageId, l.ToPageId, l.Kind }).IsUnique();
            e.HasIndex(l => l.ToPageId);
            e.HasIndex(l => l.WikiId);
            e.HasOne(l => l.FromPage).WithMany().HasForeignKey(l => l.FromPageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.ToPage).WithMany().HasForeignKey(l => l.ToPageId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<WikiPageSource>(e =>
        {
            e.Property(s => s.SourceKey).HasMaxLength(1200);
            e.HasIndex(s => s.DocumentId);
            e.HasOne(s => s.Page).WithMany(p => p.Sources).HasForeignKey(s => s.PageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Document).WithMany().HasForeignKey(s => s.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<WikiSource>(e =>
        {
            e.Property(s => s.SourceKey).HasMaxLength(1200);
            e.Property(s => s.Fingerprint).HasMaxLength(64);
            e.Property(s => s.Type).HasMaxLength(10);
            e.Property(s => s.Extraction).HasColumnType("jsonb");
            e.HasIndex(s => new { s.WikiId, s.SourceKey }).IsUnique();
            e.HasOne(s => s.Wiki).WithMany().HasForeignKey(s => s.WikiId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Document).WithMany().HasForeignKey(s => s.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<WikiRun>(e =>
        {
            e.Property(r => r.Mode).HasMaxLength(20);
            e.Property(r => r.Status).HasMaxLength(20);
            e.Property(r => r.Stage).HasMaxLength(30);
            e.Property(r => r.Error).HasMaxLength(500);
            e.Property(r => r.Stats).HasColumnType("jsonb");
            // At most one queued or running build per wiki, enforced by the database rather than by process memory.
            e.HasIndex(r => r.WikiId).IsUnique().HasFilter("\"Status\" IN ('queued', 'running')").HasDatabaseName("IX_WikiRuns_Active");
            e.HasIndex(r => new { r.WikiId, r.Id });
            e.HasOne(r => r.Wiki).WithMany().HasForeignKey(r => r.WikiId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
