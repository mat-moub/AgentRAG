using AgentRag.Api.Data;
using AgentRag.Api.Services;

public class WikiDeltaTests
{
    private static ScopeDocument Upload(int id, string title = "Spec") => new(id, title, null, null, null);
    private static ScopeDocument File(int id, string path, string? sha) => new(id, path, 1, path, sha);
    private static WikiSource Row(string key, int? documentId, string fingerprint, string type = "document") =>
        new() { SourceKey = key, DocumentId = documentId, Fingerprint = fingerprint, Type = type };

    [Fact]
    public void Compute_RepositoryFileReplacedUnderNewId_IsChangedNotRemovedAndAdded()
    {
        var manifest = new[] { Row("repo:1:src/A.cs", 10, "sha-1", "code") };

        var delta = WikiDelta.Compute([File(11, "src/A.cs", "sha-2")], manifest);

        var (row, document) = Assert.Single(delta.Changed);
        Assert.Equal((10, 11), (row.DocumentId, document.Id));
        Assert.Empty(delta.Added);
        Assert.Empty(delta.Removed);
        Assert.Empty(delta.Relinked);
        Assert.Equal(0, delta.Unchanged);
    }

    [Fact]
    public void Compute_SameShaUnderNewId_IsRelinkedAndCountedUnchanged()
    {
        var manifest = new[] { Row("repo:1:src/A.cs", 10, "sha-1", "code") };

        var delta = WikiDelta.Compute([File(12, "src/A.cs", "sha-1")], manifest);

        var (row, document) = Assert.Single(delta.Relinked);
        Assert.Equal((10, 12), (row.DocumentId, document.Id));
        Assert.Empty(delta.Changed);
        Assert.Empty(delta.Added);
        Assert.Empty(delta.Removed);
        Assert.Equal(1, delta.Unchanged);
    }

    [Fact]
    public void Compute_SameFileSameId_IsUnchanged()
    {
        var delta = WikiDelta.Compute([File(10, "src/A.cs", "sha-1"), Upload(5)], [Row("repo:1:src/A.cs", 10, "sha-1", "code"), Row("doc:5", 5, "5")]);

        Assert.Equal(2, delta.Unchanged);
        Assert.Empty(delta.Added);
        Assert.Empty(delta.Changed);
        Assert.Empty(delta.Removed);
        Assert.Empty(delta.Relinked);
    }

    [Fact]
    public void Compute_NewAndMissingDocuments_AreAddedAndRemoved()
    {
        var manifest = new[] { Row("doc:5", 5, "5"), Row("repo:1:gone.cs", 7, "x", "code") };

        var delta = WikiDelta.Compute([Upload(9, "New"), File(8, "src/B.cs", "sha-b")], manifest);

        Assert.Equal([8, 9], delta.Added.Select(d => d.Id));
        Assert.Equal(["doc:5", "repo:1:gone.cs"], delta.Removed.Select(r => r.SourceKey).Order());
        Assert.Empty(delta.Changed);
    }

    [Fact]
    public void Compute_UploadedDocumentIsKeyedById_SoAReplacementIsRemovedAndAdded()
    {
        var delta = WikiDelta.Compute([Upload(6)], [Row("doc:5", 5, "5")]);

        Assert.Equal(6, Assert.Single(delta.Added).Id);
        Assert.Equal("doc:5", Assert.Single(delta.Removed).SourceKey);
        Assert.Empty(delta.Changed);
    }

    [Fact]
    public void Compute_TwoDocumentsForOnePath_KeepsTheNewest()
    {
        var delta = WikiDelta.Compute([File(3, "src/A.cs", "old"), File(4, "src/A.cs", "new")], []);

        Assert.Equal(4, Assert.Single(delta.Added).Id);
    }

    [Fact]
    public void Compute_DuplicateManifestRows_AreRemovedOnce()
    {
        var delta = WikiDelta.Compute([Upload(5)], [Row("doc:5", 5, "5"), Row("doc:5", 5, "5")]);

        Assert.Equal(1, delta.Unchanged);
        Assert.Single(delta.Removed);
    }

    [Fact]
    public void KeyFingerprintAndType_DistinguishCodeFromProse()
    {
        Assert.Equal("repo:1:src/A.cs", WikiDelta.Key(File(1, "src/A.cs", "sha")));
        Assert.Equal("doc:7", WikiDelta.Key(Upload(7)));
        Assert.Equal("sha", WikiDelta.Fingerprint(File(1, "src/A.cs", "sha")));
        Assert.Equal("7", WikiDelta.Fingerprint(Upload(7)));
        Assert.Equal(WikiDelta.Code, WikiDelta.Type(File(1, "src/A.cs", "sha")));
        Assert.Equal(WikiDelta.Document, WikiDelta.Type(File(1, "docs/guide.md", "sha")));
        Assert.Equal(WikiDelta.Document, WikiDelta.Type(Upload(7, "Program.cs")));
    }
}
