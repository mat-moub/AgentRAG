using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AgentRag.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();
    public DbSet<DocumentChunk> Chunks => Set<DocumentChunk>();
    public DbSet<DocumentText> DocumentTexts => Set<DocumentText>();
    public DbSet<KnowledgeFolder> Folders => Set<KnowledgeFolder>();
    public DbSet<FolderMember> FolderMembers => Set<FolderMember>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
        builder.HasPostgresExtension("vector");
        builder.Entity<DocumentChunk>().Property(c => c.Embedding).HasColumnType("vector");
        builder.Entity<KnowledgeDocument>(e =>
        {
            e.Property(d => d.Title).HasMaxLength(256);
            e.HasOne(d => d.Owner).WithMany().HasForeignKey(d => d.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(d => d.Chunks).WithOne(c => c.Document).HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Folder).WithMany().HasForeignKey(d => d.FolderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Text).WithOne(t => t.Document).HasForeignKey<DocumentText>(t => t.DocumentId).OnDelete(DeleteBehavior.Cascade);
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
