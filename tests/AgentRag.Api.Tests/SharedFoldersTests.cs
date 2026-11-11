using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AgentRag.Api.Controllers;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

public class SharedFoldersTests
{
    [Fact]
    public async Task Reader_OnlySeesSharedSubtree_WithoutPrivateAncestorsOrMembers()
    {
        await using var host = await Host.StartAsync();
        using var bob = host.Client("bob");
        var library = (await bob.GetFromJsonAsync<LibraryDto>("/api/library"))!;
        Assert.Equal(new[] { 2, 3, 5 }, library.Folders.Select(f => f.Id).Order().ToArray());
        var shared = library.Folders.Single(f => f.Id == 2);
        Assert.Null(shared.ParentId);
        Assert.Equal("Contracts", shared.Path);
        Assert.Equal("read", shared.Role);
        Assert.False(shared.CanWrite);
        Assert.False(shared.CanManage);
        Assert.Equal(2, shared.DocumentCount);
        Assert.Equal("Contracts / Annexes", library.Folders.Single(f => f.Id == 3).Path);
        Assert.Equal(new[] { 11, 12, 14 }, library.Documents.Select(d => d.Id).Order().ToArray());
        Assert.False(library.Documents.Single(d => d.Id == 11).CanWrite);
        Assert.DoesNotContain("Private parent", await bob.GetStringAsync("/api/library"));
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/library/folders/2/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/library/folders/1/members")).StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/library/folders", "{\"name\":\"No\",\"parentId\":2}")]
    [InlineData("PATCH", "/api/library/folders/3", "{\"name\":\"No\"}")]
    [InlineData("DELETE", "/api/library/folders/3", null)]
    [InlineData("DELETE", "/api/documents/11", null)]
    [InlineData("PATCH", "/api/documents/11", "{\"name\":\"No\"}")]
    [InlineData("POST", "/api/documents/11/move", "{\"folderId\":3}")]
    [InlineData("POST", "/api/documents", "{\"title\":\"No\",\"content\":\"Text\",\"folderId\":2}")]
    [InlineData("POST", "/api/library/folders/2/members", "{\"email\":\"eve@example.test\",\"role\":\"write\"}")]
    public async Task Reader_CannotMutateEvenThroughDirectApi(string method, string path, string? body)
    {
        await using var host = await Host.StartAsync();
        using var client = host.Client("bob");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Writer_CanContributeAndManageContent_ButCannotManageSharedRoot()
    {
        await using var host = await Host.StartAsync();
        using var writer = host.Client("writer");
        Assert.True((await writer.PostAsJsonAsync("/api/library/folders", new { name = "New annex", parentId = 3 })).IsSuccessStatusCode);
        Assert.True((await writer.PatchAsJsonAsync("/api/library/folders/3", new { name = "Updated annexes" })).IsSuccessStatusCode);
        Assert.True((await writer.PostAsJsonAsync("/api/documents/11/move", new { folderId = 3 })).IsSuccessStatusCode);
        var imported = await writer.PostAsJsonAsync("/api/documents", new { title = "Contribution", content = "Shared content", folderId = 3 });
        Assert.True(imported.IsSuccessStatusCode, await imported.Content.ReadAsStringAsync());
        var document = (await imported.Content.ReadFromJsonAsync<DocumentDto>())!;
        await host.DatabaseAsync(async db =>
        {
            var saved = await db.Documents.SingleAsync(d => d.Id == document.Id);
            Assert.Equal("alice", saved.OwnerId);
            Assert.Equal("writer", saved.AddedById);
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.DeleteAsync("/api/library/folders/2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PatchAsJsonAsync("/api/library/folders/2", new { name = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.DeleteAsync("/api/library/folders/2/sharing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync("/api/documents/12/move", new { folderId = (int?)null, confirmAccessChange = true })).StatusCode);
    }

    [Fact]
    public async Task Sharing_RejectsOverlapsAndNonAccounts_AndSupportsRoleChangeAndRevocation()
    {
        await using var host = await Host.StartAsync();
        using var alice = host.Client("alice");
        using var bob = host.Client("bob");
        var before = (await bob.GetFromJsonAsync<LibraryDto>("/api/library"))!.Revision;
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/library/folders/1/members", new { email = "eve@example.test", role = "read" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/library/folders/3/members", new { email = "eve@example.test", role = "read" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/library/folders/4/members", new { email = "missing@example.test", role = "read" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/library/folders/4/members", new { email = "eve@example.test", role = "admin" })).StatusCode);
        Assert.True((await alice.PatchAsJsonAsync("/api/library/folders/2/members/bob", new { role = "write" })).IsSuccessStatusCode);
        var changed = (await bob.GetFromJsonAsync<LibraryDto>("/api/library"))!;
        Assert.NotEqual(before, changed.Revision);
        Assert.True(changed.Folders.Single(f => f.Id == 3).CanWrite);
        Assert.True((await alice.DeleteAsync("/api/library/folders/2/members/bob")).IsSuccessStatusCode);
        var revoked = (await bob.GetFromJsonAsync<LibraryDto>("/api/library"))!;
        Assert.DoesNotContain(revoked.Folders, f => f.Id is 2 or 3);
        Assert.DoesNotContain(revoked.Documents, d => d.Id is 11 or 12);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync("/api/explore", new { folderId = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync("/api/chat", new { message = "Hello", folderId = 2 })).StatusCode);
    }

    [Fact]
    public async Task Moves_RequireConfirmationForChangedAccess_AndRejectCyclesAndOwnershipTransfer()
    {
        await using var host = await Host.StartAsync();
        using var alice = host.Client("alice");
        using var bob = host.Client("bob");
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/documents/10/move", new { folderId = 2 })).StatusCode);
        Assert.True((await alice.PostAsJsonAsync("/api/documents/10/move", new { folderId = 2, confirmAccessChange = true })).IsSuccessStatusCode);
        Assert.Contains((await bob.GetFromJsonAsync<LibraryDto>("/api/library"))!.Documents, d => d.Id == 10);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/library/folders/1/move", new { folderId = 3, confirmAccessChange = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/library/folders/4/move", new { folderId = 2 })).StatusCode);
        Assert.True((await alice.PostAsJsonAsync("/api/library/folders/4/move", new { folderId = 2, confirmAccessChange = true })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync("/api/library/folders/5/members", new { email = "alice@example.test", role = "write" })).StatusCode);
        await host.DatabaseAsync(async db => { db.FolderMembers.Add(new FolderMember { FolderId = 5, UserId = "alice", Role = "write" }); await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/documents/10/move", new { folderId = 5, confirmAccessChange = true })).StatusCode);
    }

    [Fact]
    public async Task LeaveAndStopSharing_PreserveContent_WhileRecursiveDeleteRemovesOnlySubtree()
    {
        await using var host = await Host.StartAsync();
        using var alice = host.Client("alice");
        using var bob = host.Client("bob");
        Assert.True((await bob.DeleteAsync("/api/library/folders/2/membership")).IsSuccessStatusCode);
        Assert.DoesNotContain((await bob.GetFromJsonAsync<LibraryDto>("/api/library"))!.Documents, d => d.Id == 11);
        Assert.Contains((await alice.GetFromJsonAsync<LibraryDto>("/api/library"))!.Documents, d => d.Id == 11);
        Assert.True((await alice.DeleteAsync("/api/library/folders/2/sharing")).IsSuccessStatusCode);
        Assert.True((await alice.PostAsJsonAsync("/api/library/folders/1/members", new { email = "eve@example.test", role = "read" })).IsSuccessStatusCode);
        Assert.True((await alice.DeleteAsync("/api/library/folders/2")).IsSuccessStatusCode);
        var remaining = (await alice.GetFromJsonAsync<LibraryDto>("/api/library"))!;
        Assert.DoesNotContain(remaining.Folders, f => f.Id is 2 or 3);
        Assert.DoesNotContain(remaining.Documents, d => d.Id is 11 or 12);
        Assert.Contains(remaining.Documents, d => d.Id == 13);
        Assert.Contains(remaining.Documents, d => d.Id == 10);
    }

    [Fact]
    public async Task RetrievalPolicy_IncludesDescendantsAndExcludesSiblingAndUnfiledDocuments()
    {
        await using var host = await Host.StartAsync();
        await host.DatabaseAsync(async db =>
        {
            var policy = new LibraryAccess(db);
            var query = await policy.ReadableDocumentsAsync("bob", 2);
            Assert.Equal(new[] { 11, 12 }, await query.OrderBy(d => d.Id).Select(d => d.Id).ToArrayAsync());
            await Assert.ThrowsAsync<LibraryException>(() => policy.ReadableDocumentsAsync("bob", 1));
            var scoped = await policy.ReadableDocumentsAsync("alice", 3);
            Assert.Equal(12, await scoped.Select(d => d.Id).SingleAsync());
        });
    }

    [Fact]
    public async Task RevokedWriter_CannotPublishAnImportStartedBeforeRevocation()
    {
        await using var host = await Host.StartAsync();
        host.Generator.DuringGeneration = () => host.DatabaseAsync(async db =>
        {
            db.FolderMembers.Remove(await db.FolderMembers.SingleAsync(m => m.FolderId == 2 && m.UserId == "writer"));
            await db.SaveChangesAsync();
        });
        using var writer = host.Client("writer");
        var response = await writer.PostAsJsonAsync("/api/documents", new { title = "Must not publish", content = "Text", folderId = 3 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await host.DatabaseAsync(async db => Assert.False(await db.Documents.AnyAsync(d => d.Title == "Must not publish")));
    }

    [Fact]
    public async Task ChangedSources_RejectOldConversationHistory_BeforeCallingModel()
    {
        await using var host = await Host.StartAsync();
        using var bob = host.Client("bob");
        using var alice = host.Client("alice");
        var revision = (await bob.GetFromJsonAsync<LibraryDto>("/api/library"))!.Revision;
        await alice.DeleteAsync("/api/library/folders/2/members/bob");
        var response = await bob.PostAsJsonAsync("/api/chat/stream", new
        {
            message = "Continue", libraryRevision = revision,
            history = new[] { new { role = "assistant", content = "Previously accessible text" } }
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public void AuthorizedRetrievalAndSampling_TranslateToBoundedPostgresQueries()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", o => o.UseVector()).Options);
        var snapshot = new LibrarySnapshot("bob", [new() { Id = 2, Name = "Shared", OwnerId = "alice" }],
            [new() { FolderId = 2, UserId = "bob", Role = "read" }]);
        var sql = new RagService(db, null!).SearchChunks(snapshot.Documents(db, 2), new Vector(new float[] { 1, 0 }), 6).ToQueryString();
        Assert.Contains("FolderId", sql);
        Assert.Contains("<=>", sql);
        Assert.Contains("LIMIT", sql);
        var sampling = PassageSampling.SelectAccessibleIds(db, [11, 12]).ToQueryString();
        Assert.Contains("ANY", sampling);
        Assert.Contains("PARTITION BY", sampling);
        Assert.Contains("500", sampling);
        Assert.DoesNotContain("Embedding", sampling);
    }

    private sealed class TestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<DocumentChunk>().Ignore(c => c.Embedding);
        }
    }

    private sealed class Generator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Func<Task>? DuringGeneration { get; set; }
        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (DuringGeneration is { } callback) await callback();
            return new(values.Select(_ => new Embedding<float>(new float[] { 1, 0 })));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class UnusedChatClient : IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Model must not be called in authorization tests.");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Model must not be called in authorization tests.");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Host(WebApplication app, Generator generator) : IAsyncDisposable
    {
        public Generator Generator { get; } = generator;
        public HttpClient Client(string user)
        {
            var client = app.GetTestClient();
            var options = app.Services.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>().Get(IdentityConstants.BearerScheme);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], IdentityConstants.BearerScheme));
            var token = options.BearerTokenProtector.Protect(new AuthenticationTicket(principal,
                new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30) }, IdentityConstants.BearerScheme));
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            return client;
        }
        public async Task DatabaseAsync(Func<AppDbContext, Task> operation)
        {
            using var scope = app.Services.CreateScope();
            await operation(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }
        public ValueTask DisposeAsync() => app.DisposeAsync();
        public static async Task<Host> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthentication(IdentityConstants.BearerScheme).AddBearerToken(IdentityConstants.BearerScheme);
            builder.Services.AddAuthorization();
            var name = Guid.NewGuid().ToString();
            builder.Services.AddScoped<AppDbContext>(_ => new TestDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options));
            builder.Services.AddControllers(o => o.Filters.Add<LibraryExceptionFilter>()).AddApplicationPart(typeof(LibraryController).Assembly);
            var generator = new Generator();
            builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
            builder.Services.AddOptions<EmbeddingOptions>();
            builder.Services.AddSingleton<EmbeddingService>();
            builder.Services.AddSingleton<IChatClient>(new UnusedChatClient());
            builder.Services.AddSingleton<RagAgentFactory>();
            builder.Services.AddSingleton<DocumentTextExtractor>();
            builder.Services.AddScoped<RagService>();
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
            var host = new Host(app, generator);
            await host.DatabaseAsync(async db =>
            {
                foreach (var user in new[] { "alice", "bob", "writer", "eve" })
                    db.Users.Add(new ApplicationUser { Id = user, Email = $"{user}@example.test", NormalizedEmail = $"{user}@example.test".ToUpperInvariant() });
                db.Folders.AddRange(
                    new KnowledgeFolder { Id = 1, Name = "Private parent", OwnerId = "alice" },
                    new KnowledgeFolder { Id = 2, Name = "Contracts", OwnerId = "alice", ParentId = 1 },
                    new KnowledgeFolder { Id = 3, Name = "Annexes", OwnerId = "alice", ParentId = 2 },
                    new KnowledgeFolder { Id = 4, Name = "Private sibling", OwnerId = "alice", ParentId = 1 },
                    new KnowledgeFolder { Id = 5, Name = "Bob private", OwnerId = "bob" });
                db.FolderMembers.AddRange(new FolderMember { FolderId = 2, UserId = "bob", Role = "read" }, new FolderMember { FolderId = 2, UserId = "writer", Role = "write" });
                db.Documents.AddRange(
                    new KnowledgeDocument { Id = 10, Title = "Unfiled secret", OwnerId = "alice" },
                    new KnowledgeDocument { Id = 11, Title = "Shared contract", OwnerId = "alice", FolderId = 2 },
                    new KnowledgeDocument { Id = 12, Title = "Shared annex", OwnerId = "alice", FolderId = 3 },
                    new KnowledgeDocument { Id = 13, Title = "Sibling secret", OwnerId = "alice", FolderId = 4 },
                    new KnowledgeDocument { Id = 14, Title = "Bob document", OwnerId = "bob" });
                await db.SaveChangesAsync();
            });
            await app.StartAsync();
            return host;
        }
    }
}
