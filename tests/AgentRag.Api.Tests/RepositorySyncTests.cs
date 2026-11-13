using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRag.Api.Controllers;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public class RepositorySyncTests
{
    private const string Token = "github_pat_secret-value";
    private static readonly string Code = string.Concat(Enumerable.Range(1, 120).Select(i => i == 77 ? "    var needle = Compute();\n" : $"    // line {i} of the file\n"));

    [Fact]
    public async Task Sync_FirstRun_MirrorsTextFiles_SplitsCodeOnLinesAndEmbedsOnlyDocumentation()
    {
        using var test = new Fixture();
        test.GitHub.Files["README.md"] = "# Product\n\nThe product guide.";
        test.GitHub.Files["src/App.cs"] = Code;
        test.GitHub.Files["node_modules/lib/index.js"] = "ignored();";
        test.GitHub.Files["assets/logo.png"] = "not text";

        var summary = await test.Sync.SyncAsync("alice", test.RepositoryId);

        Assert.Equal((2, 0, 0, 0, 0), (summary.Added, summary.Updated, summary.Removed, summary.Unchanged, summary.Skipped));
        var documents = await test.Db.Documents.Include(d => d.Chunks).Include(d => d.Text).OrderBy(d => d.Title).ToListAsync();
        Assert.Equal(new[] { "README.md", "src/App.cs" }, documents.Select(d => d.Title));
        Assert.All(documents, d => Assert.Equal((test.RepositoryId, test.FolderId, "alice"), (d.RepositoryId, d.FolderId, d.OwnerId)));

        var (guide, code) = (documents[0], documents[1]);
        Assert.True(guide.Indexed);
        Assert.Equal(RagService.ChunkOverlap, guide.PassageOverlap);
        Assert.False(code.Indexed);
        Assert.Equal(0, code.PassageOverlap);
        Assert.Equal(Code, code.Text!.Content);
        var passages = code.Chunks.OrderBy(c => c.Index).ToList();
        Assert.True(passages.Count > 1);
        Assert.Equal(Code, string.Concat(passages.Select(c => c.Content)));
        Assert.Equal(1, passages[0].StartLine);
        Assert.All(passages, c => Assert.Equal(c.StartLine, 1 + Code[..Code.IndexOf(c.Content, StringComparison.Ordinal)].Count(ch => ch == '\n')));
        // Only the documentation file reached the embedding model.
        Assert.Equal(new[] { "# Product\n\nThe product guide." }, test.Embeddings.Inputs);

        var repository = await test.Db.Repositories.AsNoTracking().SingleAsync();
        Assert.Equal((test.GitHub.Commit, (string?)null), (repository.LastSyncedCommit, repository.LastError));
        Assert.NotNull(repository.LastSyncedUtc);
    }

    [Fact]
    public async Task Sync_SecondRun_FetchesOnlyChangedFiles_AndRemovesDeletedOnes()
    {
        using var test = new Fixture();
        test.GitHub.Files["src/Kept.cs"] = "class Kept {}\n";
        test.GitHub.Files["src/Changed.cs"] = "class Changed {}\n";
        test.GitHub.Files["src/Deleted.cs"] = "class Deleted {}\n";
        await test.Sync.SyncAsync("alice", test.RepositoryId);
        var kept = await test.Db.Documents.AsNoTracking().SingleAsync(d => d.SourcePath == "src/Kept.cs");
        test.GitHub.BlobRequests.Clear();

        test.GitHub.Files["src/Changed.cs"] = "class Changed { int version = 2; }\n";
        test.GitHub.Files["src/Added.cs"] = "class Added {}\n";
        test.GitHub.Files.Remove("src/Deleted.cs");
        var summary = await test.Sync.SyncAsync("alice", test.RepositoryId);

        Assert.Equal((1, 1, 1, 1), (summary.Added, summary.Updated, summary.Removed, summary.Unchanged));
        Assert.Equal(new[] { "src/Added.cs", "src/Changed.cs" }, test.GitHub.BlobRequests.Order());
        var documents = await test.Db.Documents.AsNoTracking().Include(d => d.Text).OrderBy(d => d.Title).ToListAsync();
        Assert.Equal(new[] { "src/Added.cs", "src/Changed.cs", "src/Kept.cs" }, documents.Select(d => d.Title));
        Assert.Contains("version = 2", documents[1].Text!.Content);
        Assert.Equal(kept.Id, documents[2].Id);

        test.GitHub.BlobRequests.Clear();
        var again = await test.Sync.SyncAsync("alice", test.RepositoryId);
        Assert.Equal((0, 0, 0, 3), (again.Added, again.Updated, again.Removed, again.Unchanged));
        Assert.Empty(test.GitHub.BlobRequests);
    }

    [Fact]
    public async Task Sync_ManyChangedFiles_DownloadsOneArchiveInsteadOfEachFile()
    {
        using var test = new Fixture();
        for (var i = 0; i <= RepositorySyncService.ArchiveThreshold; i++) test.GitHub.Files[$"src/File{i:000}.cs"] = $"class File{i} {{}}\n";

        var summary = await test.Sync.SyncAsync("alice", test.RepositoryId);

        Assert.Equal(RepositorySyncService.ArchiveThreshold + 1, summary.Added);
        Assert.Equal(1, test.GitHub.ArchiveRequests);
        Assert.Empty(test.GitHub.BlobRequests);
        Assert.Equal(summary.Added, await test.Db.Documents.CountAsync());
    }

    [Fact]
    public async Task Sync_BinaryContentBehindTextExtension_IsSkipped()
    {
        using var test = new Fixture();
        test.GitHub.Files["notes.txt"] = "binary\0content";
        test.GitHub.Files["src/App.cs"] = "class App {}\n";

        var summary = await test.Sync.SyncAsync("alice", test.RepositoryId);

        Assert.Equal((1, 1), (summary.Added, summary.Skipped));
        Assert.Equal("src/App.cs", (await test.Db.Documents.AsNoTracking().SingleAsync()).Title);
    }

    [Fact]
    public async Task Sync_ReaderOrStranger_IsRefusedBeforeCallingGitHub()
    {
        using var test = new Fixture();
        test.GitHub.Files["src/App.cs"] = "class App {}\n";
        test.Db.FolderMembers.Add(new FolderMember { FolderId = test.FolderId, UserId = "reader", Role = "read" });
        await test.Db.SaveChangesAsync();

        Assert.Equal(403, (await Assert.ThrowsAsync<LibraryException>(() => test.Sync.SyncAsync("reader", test.RepositoryId))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<LibraryException>(() => test.Sync.SyncAsync("stranger", test.RepositoryId))).Status);
        Assert.Equal(0, test.GitHub.TreeRequests);
        Assert.Empty(await test.Db.Documents.ToListAsync());
    }

    [Fact]
    public async Task Sync_GitHubFailure_IsRecordedOnTheRepositoryWithoutLeakingTheToken()
    {
        using var test = new Fixture();
        test.GitHub.Failure = new GitHubException("GitHub rejected the access token. Check that it is valid and not expired.", 400);

        await Assert.ThrowsAsync<GitHubException>(() => test.Sync.SyncAsync("alice", test.RepositoryId));

        var repository = await test.Db.Repositories.AsNoTracking().SingleAsync();
        Assert.Contains("rejected the access token", repository.LastError);
        Assert.Null(repository.LastSyncedCommit);
        Assert.DoesNotContain(Token, repository.ProtectedToken);
    }

    [Fact]
    public async Task Search_AfterSync_GrepReportsFilePathAndLine_AndReadingReturnsExactCode()
    {
        using var test = new Fixture();
        test.GitHub.Files["src/App.cs"] = Code;
        test.GitHub.Files["web/app.ts"] = "const needle = 1\n";
        await test.Sync.SyncAsync("alice", test.RepositoryId);
        var rag = new RagService(test.Db, null!);

        var grep = await rag.GrepAsync("alice", "needle", path: "src/*.cs");

        var match = Assert.Single(grep.Matches);
        Assert.Equal(("src/App.cs", 77), (match.DocumentTitle, match.Line));
        Assert.Equal(2, (await rag.GrepAsync("alice", "needle")).TotalPassages);
        Assert.Equal(0, (await rag.GrepAsync("bob", "needle")).TotalPassages);

        var read = await rag.ReadPassagesAsync("alice", match.DocumentId, match.PassageNumber, 2);
        var total = (await test.Db.Chunks.Where(c => c.DocumentId == match.DocumentId).ToListAsync()).OrderBy(c => c.Index).ToList();
        Assert.Equal(total[match.PassageNumber - 1].StartLine, read.FirstLine);
        // Line-split passages have no overlap to remove: the text is the file's, character for character.
        Assert.Equal(string.Concat(total.Skip(match.PassageNumber - 1).Take(2).Select(c => c.Content)), read.Text);
        Assert.Contains(read.Text, Code);
    }

    [Fact]
    public async Task Connect_StoresEncryptedToken_AndNeverReturnsIt()
    {
        using var test = new Fixture(connect: false);
        var controller = test.Controller("alice");

        var created = (await controller.Connect(new("https://github.com/acme/shop.git", Token), CancellationToken.None)).Value!;

        Assert.Equal(("acme/shop", "main", true), (created.Repository, created.Branch, created.CanSync));
        var repository = await test.Db.Repositories.Include(r => r.Folder).SingleAsync();
        Assert.NotEqual(Token, repository.ProtectedToken);
        Assert.Equal(Token, test.Tokens.Unprotect(repository.ProtectedToken));
        Assert.Equal(("acme/shop", "alice"), (repository.Folder!.Name, repository.Folder.OwnerId));
        Assert.Equal(Token, test.GitHub.LastToken);
        var listed = JsonSerializer.Serialize(await controller.List(CancellationToken.None), JsonSerializerOptions.Web);
        Assert.Contains("acme/shop", listed);
        Assert.DoesNotContain("token", listed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(created, JsonSerializerOptions.Web));
        Assert.Empty(await test.Controller("bob").List(CancellationToken.None));
    }

    [Theory]
    [InlineData("not a repository")]
    [InlineData("acme/shop/extra")]
    [InlineData("https://evil.example/acme/shop")]
    [InlineData("acme/..")]
    public async Task Connect_InvalidRepository_IsRefusedWithoutCallingGitHub(string repository)
    {
        using var test = new Fixture(connect: false);
        var error = await Assert.ThrowsAsync<LibraryException>(() => test.Controller("alice").Connect(new(repository, Token), CancellationToken.None));
        Assert.Equal(400, error.Status);
        Assert.Null(test.GitHub.LastToken);
    }

    [Fact]
    public async Task Connect_TokenRejectedByGitHub_StoresNothing()
    {
        using var test = new Fixture(connect: false);
        test.GitHub.Failure = new GitHubException("GitHub rejected the access token. Check that it is valid and not expired.", 400);
        await Assert.ThrowsAsync<LibraryException>(() => test.Controller("alice").Connect(new("acme/shop", Token), CancellationToken.None));
        Assert.Empty(await test.Db.Repositories.ToListAsync());
        Assert.Empty(await test.Db.Folders.ToListAsync());
    }

    [Fact]
    public async Task SyncEndpoint_StreamsProgressAndSummary_AndDeleteRemovesTheMirroredFiles()
    {
        using var test = new Fixture();
        test.GitHub.Files["src/App.cs"] = "class App {}\n";
        var controller = test.Controller("alice");

        await controller.Sync(test.RepositoryId, CancellationToken.None);

        var events = Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<RepositorySyncEvent>(line, JsonSerializerOptions.Web)!).ToList();
        Assert.Equal("application/x-ndjson", controller.Response.ContentType);
        Assert.Contains(events, e => e.Type == "progress");
        Assert.Equal(("completed", 1), (events[^1].Type, events[^1].Summary!.Added));

        await Assert.ThrowsAsync<LibraryException>(() => test.Controller("bob").Delete(test.RepositoryId, CancellationToken.None));
        await test.Controller("alice").Delete(test.RepositoryId, CancellationToken.None);
        Assert.Empty(await test.Db.Documents.ToListAsync());
        Assert.Empty(await test.Db.Repositories.ToListAsync());
        Assert.Single(await test.Db.Folders.ToListAsync());
    }

    [Fact]
    public void ChunkLines_KeepsExactTextAndLineNumbers_AndSplitsOverlongLines()
    {
        var text = "first\n" + new string('x', 25) + "\nthird 🙂\n\nfifth";

        var chunks = RagService.ChunkLines(text, size: 10);

        Assert.Equal(text, string.Concat(chunks.Select(c => c.Content)));
        Assert.All(chunks, c => Assert.True(c.Content.Length <= 10));
        Assert.Equal(("first\n", 1), chunks[0]);
        // The 25-character line does not fit: its pieces all carry line 2.
        Assert.Equal(new[] { 2, 2, 2 }, chunks.Skip(1).Take(3).Select(c => c.StartLine));
        Assert.All(chunks, c => Assert.False(char.IsHighSurrogate(c.Content[^1])));
        Assert.Equal(5, chunks[^1].StartLine);
        Assert.Empty(RagService.ChunkLines(""));
        Assert.Equal(new[] { ("one line", 1) }, RagService.ChunkLines("one line"));
    }

    [Theory]
    [InlineData("src/Api/Program.cs", 100, true)]
    [InlineData("README.md", 100, true)]
    [InlineData("Dockerfile", 100, true)]
    [InlineData(".gitignore", 100, true)]
    [InlineData("web/node_modules/react/index.js", 100, false)]
    [InlineData("src/bin/Debug/app.json", 100, false)]
    [InlineData("web/package-lock.json", 100, false)]
    [InlineData("web/dist.min.js", 100, false)]
    [InlineData("Migrations/Init.Designer.cs", 100, false)]
    [InlineData("assets/logo.png", 100, false)]
    [InlineData("tools/app.exe", 100, false)]
    [InlineData("unknown", 100, false)]
    [InlineData("src/Empty.cs", 0, false)]
    [InlineData("src/Huge.cs", RepositoryFiles.MaxFileBytes + 1, false)]
    public void IsEligible_KeepsKnownTextFilesOutsideGeneratedFolders(string path, long size, bool expected) =>
        Assert.Equal(expected, RepositoryFiles.IsEligible(path, size));

    [Fact]
    public void Title_LongPath_KeepsTheFileName()
    {
        var path = string.Join('/', Enumerable.Repeat("directory", 40)) + "/Target.cs";
        var title = RepositoryFiles.Title(path);
        Assert.Equal(256, title.Length);
        Assert.StartsWith("…", title);
        Assert.EndsWith("/Target.cs", title);
    }

    [Theory]
    [InlineData("acme/shop", "acme", "shop")]
    [InlineData(" https://github.com/Acme/shop.api/ ", "Acme", "shop.api")]
    [InlineData("github.com/acme/shop.git", "acme", "shop")]
    public void TryParseRepository_AcceptsNamesAndGitHubAddresses(string value, string owner, string name)
    {
        Assert.True(GitHubClient.TryParseRepository(value, out var parsedOwner, out var parsedName));
        Assert.Equal((owner, name), (parsedOwner, parsedName));
    }

    [Fact]
    public async Task GitHubClient_ReadsTreeBlobAndArchive_SendingTheTokenOnlyToTheApiHost()
    {
        var archive = new MemoryStream();
        await using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        await using (var tar = new TarWriter(gzip))
            foreach (var (path, content) in new[] { ("acme-shop-abc/src/App.cs", "class App {}"), ("acme-shop-abc/docs/skip.md", "skip") })
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request);
            return request.RequestUri!.AbsolutePath switch
            {
                "/repos/acme/shop/branches/feature%2Fsearch" => Json("""{"commit":{"sha":"abc"}}"""),
                "/repos/acme/shop/git/trees/abc" => Json("""{"truncated":false,"tree":[{"path":"src","type":"tree","sha":"t1"},{"path":"src/App.cs","type":"blob","sha":"b1","size":12}]}"""),
                "/repos/acme/shop/git/blobs/b1" => new(HttpStatusCode.OK) { Content = new ByteArrayContent("class App {}"u8.ToArray()) },
                "/repos/acme/shop/tarball/abc" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) },
                _ => new(HttpStatusCode.NotFound)
            };
        }));
        var client = new GitHubClient(http);

        var tree = await client.GetTreeAsync("acme", "shop", "feature/search", Token, CancellationToken.None);
        Assert.Equal(("abc", false), (tree.CommitSha, tree.Truncated));
        Assert.Equal(new GitHubFile("src/App.cs", "b1", 12), Assert.Single(tree.Files));
        Assert.Equal("class App {}", Encoding.UTF8.GetString(await client.GetBlobAsync("acme", "shop", "b1", Token, CancellationToken.None)));
        var files = new List<(string Path, byte[] Content)>();
        await foreach (var file in client.ReadArchiveAsync("acme", "shop", "abc", Token, new HashSet<string> { "src/App.cs" }, CancellationToken.None)) files.Add(file);
        Assert.Equal(("src/App.cs", "class App {}"), (Assert.Single(files).Path, Encoding.UTF8.GetString(files[0].Content)));

        Assert.All(requests, r => Assert.Equal(("api.github.com", "https", "Bearer " + Token), (r.RequestUri!.Host, r.RequestUri.Scheme, r.Headers.Authorization!.ToString())));
        var missing = await Assert.ThrowsAsync<GitHubException>(() => client.GetDefaultBranchAsync("acme", "missing", Token, CancellationToken.None));
        Assert.Equal(404, missing.Status);
        Assert.DoesNotContain(Token, missing.Message);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class Fixture : IDisposable
    {
        public TestDb Db { get; } = new(Guid.NewGuid().ToString());
        public FakeGitHub GitHub { get; } = new();
        public RecordingEmbeddings Embeddings { get; } = new();
        public RepositoryTokenProtector Tokens { get; } = new(new EphemeralDataProtectionProvider());
        public RepositorySyncService Sync { get; }
        public int RepositoryId { get; }
        public int FolderId { get; }
        private readonly EmbeddingService embeddingService;

        public Fixture(bool connect = true)
        {
            embeddingService = new(Embeddings, Options.Create(new EmbeddingOptions()), NullLogger<EmbeddingService>.Instance);
            Sync = new(Db, GitHub, embeddingService, Tokens, NullLogger<RepositorySyncService>.Instance);
            if (!connect) return;
            var repository = new RepositoryConnection
            {
                Folder = new KnowledgeFolder { Name = "acme/shop", OwnerId = "alice" },
                RepositoryOwner = "acme", RepositoryName = "shop", Branch = "main", ProtectedToken = Tokens.Protect(Token)
            };
            Db.Repositories.Add(repository);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            (RepositoryId, FolderId) = (repository.Id, repository.FolderId);
        }

        public RepositoriesController Controller(string user)
        {
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"))
            };
            context.Response.Body = new MemoryStream();
            return new(Db, GitHub, Tokens, Sync, NullLogger<RepositoriesController>.Instance) { ControllerContext = new ControllerContext { HttpContext = context } };
        }

        public void Dispose()
        {
            Db.Dispose();
            embeddingService.Dispose();
        }
    }

    private sealed class TestDb(string name) : AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            // Vector SQL is covered by the PostgreSQL translation tests.
            builder.Entity<DocumentChunk>().Ignore(c => c.Embedding);
        }
    }

    private sealed class FakeGitHub() : GitHubClient(new HttpClient())
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public List<string> BlobRequests { get; } = [];
        public int TreeRequests { get; private set; }
        public int ArchiveRequests { get; private set; }
        public GitHubException? Failure { get; set; }
        public string? LastToken { get; private set; }
        public string Commit => Sha(string.Join('\n', Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + Sha(f.Value))));

        public override Task<string> GetDefaultBranchAsync(string owner, string name, string token, CancellationToken ct)
        {
            LastToken = token;
            return Failure is null ? Task.FromResult("main") : Task.FromException<string>(Failure);
        }

        public override Task<GitHubTree> GetTreeAsync(string owner, string name, string branch, string token, CancellationToken ct)
        {
            TreeRequests++;
            LastToken = token;
            return Failure is null
                ? Task.FromResult(new GitHubTree(Commit, Files.Select(f => new GitHubFile(f.Key, Sha(f.Value), Encoding.UTF8.GetByteCount(f.Value))).ToList(), false))
                : Task.FromException<GitHubTree>(Failure);
        }

        public override Task<byte[]> GetBlobAsync(string owner, string name, string sha, string token, CancellationToken ct)
        {
            var file = Files.Single(f => Sha(f.Value) == sha);
            BlobRequests.Add(file.Key);
            return Task.FromResult(Encoding.UTF8.GetBytes(file.Value));
        }

        public override async IAsyncEnumerable<(string Path, byte[] Content)> ReadArchiveAsync(string owner, string name, string commit, string token,
            IReadOnlySet<string> paths, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ArchiveRequests++;
            await Task.Yield();
            foreach (var file in Files.Where(f => paths.Contains(f.Key))) yield return (file.Key, Encoding.UTF8.GetBytes(file.Value));
        }

        private static string Sha(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed class RecordingEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string> Inputs { get; } = [];
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var inputs = values.ToArray();
            Inputs.AddRange(inputs);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(_ => new Embedding<float>(new float[] { 1, 0, 0 }))));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
