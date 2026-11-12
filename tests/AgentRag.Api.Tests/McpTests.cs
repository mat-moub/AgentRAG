using System.Net;
using System.Security.Claims;
using AgentRag.Api.Data;
using AgentRag.Api.Mcp;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

public class McpTests
{
    [Fact]
    public async Task Endpoint_RejectsAnonymousInvalidExpiredTokensAndForeignOrigins()
    {
        await using var app = await StartAsync();
        using var http = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/mcp", null)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/mcp", null)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token(app, "alice", expired: true));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/mcp", null)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token(app, "alice"));
        http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("/mcp", null)).StatusCode);
    }

    [Fact]
    public async Task HttpClient_DiscoversToolsAndIsolatesUsersAcrossRequests()
    {
        await using var app = await StartAsync();
        using var http = app.GetTestClient();
        await using var alice = await ClientAsync(http, Token(app, "alice"));
        var tools = await alice.ListToolsAsync();
        Assert.Equal(new[] { "ask_rag", "grep_passages", "list_documents", "read_passages", "search_exhaustive", "search_knowledge_base" },
            tools.Select(t => t.Name).Order().ToArray());
        Assert.All(tools, t => Assert.DoesNotContain("userId", t.JsonSchema.GetRawText()));
        Assert.All(tools, t =>
        {
            Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(t.ProtocolTool.Annotations?.DestructiveHint);
            Assert.False(t.ProtocolTool.Annotations?.OpenWorldHint);
        });
        var result = Text(await alice.CallToolAsync("list_documents"));
        Assert.Contains("Alice guide", result);
        Assert.DoesNotContain("Bob secret", result);

        await using var bob = await ClientAsync(http, Token(app, "bob"));
        var other = Text(await bob.CallToolAsync("list_documents"));
        Assert.Contains("Bob secret", other);
        Assert.DoesNotContain("Alice guide", other);
        Assert.Contains("Alice guide", Text(await alice.CallToolAsync("list_documents")));
        Assert.Contains("A grounded answer", Text(await alice.CallToolAsync("ask_rag", new Dictionary<string, object?> { ["question"] = "Summarize my guide" })));
    }

    [Theory]
    [InlineData("search_knowledge_base", "query", " ")]
    [InlineData("ask_rag", "question", "")]
    [InlineData("list_documents", "limit", 101)]
    [InlineData("search_knowledge_base", "top", 21)]
    [InlineData("search_exhaustive", "top", 51)]
    [InlineData("search_exhaustive", "query", " ")]
    [InlineData("grep_passages", "pattern", "a")]
    [InlineData("grep_passages", "limit", 51)]
    [InlineData("read_passages", "from", 0)]
    [InlineData("read_passages", "count", 21)]
    public async Task Tools_InvalidArguments_ReturnToolErrors(string name, string key, object value)
    {
        await using var app = await StartAsync();
        using var http = app.GetTestClient();
        await using var client = await ClientAsync(http, Token(app, "alice"));
        var arguments = new Dictionary<string, object?> { [key] = value };
        if (key == "top") arguments["query"] = "query";
        if (key == "limit") arguments["pattern"] = "pattern";
        if (name == "read_passages") arguments["documentId"] = 1;
        var result = await client.CallToolAsync(name, arguments);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GrepAndReadPassages_ReturnOnlyTheCallersDocuments()
    {
        await using var app = await StartAsync();
        int aliceDocument, bobDocument;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alice = await db.Documents.SingleAsync(d => d.OwnerId == "alice");
            var bob = await db.Documents.SingleAsync(d => d.OwnerId == "bob");
            (aliceDocument, bobDocument) = (alice.Id, bob.Id);
            var text = string.Concat(Enumerable.Range(0, 120).Select(i => $"Sentence {i} about the SAVS service. "));
            db.Chunks.AddRange(RagService.Chunk(text).Select((content, index) => new DocumentChunk { DocumentId = alice.Id, Index = index, Content = content }));
            db.Chunks.Add(new DocumentChunk { DocumentId = bob.Id, Index = 0, Content = "Bob also wrote about the SAVS service." });
            await db.SaveChangesAsync();
        }
        using var http = app.GetTestClient();
        await using var client = await ClientAsync(http, Token(app, "alice"));

        var grep = Text(await client.CallToolAsync("grep_passages", new Dictionary<string, object?> { ["pattern"] = "savs", ["limit"] = 2 }));
        Assert.Contains("Alice guide", grep);
        Assert.Contains("\"passageNumber\":1", grep);
        Assert.Contains("\"passageNumber\":2", grep);
        Assert.DoesNotContain("\"passageNumber\":3", grep);
        Assert.DoesNotContain("Bob", grep);
        var sensitive = Text(await client.CallToolAsync("grep_passages", new Dictionary<string, object?> { ["pattern"] = "savs", ["caseSensitive"] = true }));
        Assert.Contains("\"totalPassages\":0", sensitive);
        var regex = Text(await client.CallToolAsync("grep_passages", new Dictionary<string, object?> { ["pattern"] = "Sentence 11[0-9] ", ["regex"] = true }));
        Assert.Contains("Sentence 110", regex);
        Assert.True((await client.CallToolAsync("grep_passages", new Dictionary<string, object?> { ["pattern"] = "SAVS", ["documentId"] = bobDocument })).Content
            .OfType<TextContentBlock>().All(c => c.Text.Contains("\"totalPassages\":0")));

        var read = Text(await client.CallToolAsync("read_passages", new Dictionary<string, object?> { ["documentId"] = aliceDocument, ["from"] = 1, ["count"] = 3 }));
        Assert.Contains("\"lastPassage\":3", read);
        // Consecutive passages come back as continuous text: the overlap is not repeated.
        Assert.Contains("Sentence 20 about the SAVS service. Sentence 21 about", read);
        Assert.Equal(1, read.Split("Sentence 20 about").Length - 1);
        var foreign = await client.CallToolAsync("read_passages", new Dictionary<string, object?> { ["documentId"] = bobDocument });
        Assert.True(foreign.IsError);
        Assert.DoesNotContain("Bob", Text(foreign));
    }

    [Fact]
    public async Task SharedDocuments_AppearInMcp_AndDisappearAfterRevocation()
    {
        await using var app = await StartAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Folders.Add(new KnowledgeFolder { Id = 1, Name = "Private parent", OwnerId = "alice" });
            db.Folders.Add(new KnowledgeFolder { Id = 2, Name = "Shared", ParentId = 1, OwnerId = "alice" });
            db.FolderMembers.Add(new FolderMember { FolderId = 2, UserId = "bob", Role = "read" });
            (await db.Documents.SingleAsync(d => d.OwnerId == "alice")).FolderId = 2;
            db.Documents.Add(new KnowledgeDocument { OwnerId = "alice", Title = "Private sibling" });
            await db.SaveChangesAsync();
        }
        using var http = app.GetTestClient();
        await using var bob = await ClientAsync(http, Token(app, "bob"));
        var shared = Text(await bob.CallToolAsync("list_documents", new Dictionary<string, object?> { ["folderId"] = 2 }));
        Assert.Contains("Alice guide", shared);
        Assert.DoesNotContain("Private sibling", shared);
        Assert.DoesNotContain("Bob secret", shared);
        Assert.DoesNotContain("Private parent", shared);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.FolderMembers.Remove(await db.FolderMembers.SingleAsync());
            await db.SaveChangesAsync();
        }
        Assert.DoesNotContain("Alice guide", Text(await bob.CallToolAsync("list_documents")));
        Assert.True((await bob.CallToolAsync("list_documents", new Dictionary<string, object?> { ["folderId"] = 2 })).IsError);
    }

    [Fact]
    public async Task Search_ProviderFailure_ReturnsSafeToolError()
    {
        await using var app = await StartAsync();
        using var http = app.GetTestClient();
        await using var client = await ClientAsync(http, Token(app, "alice"));
        var result = await client.CallToolAsync("search_knowledge_base", new Dictionary<string, object?> { ["query"] = "query" });
        Assert.True(result.IsError);
        Assert.DoesNotContain("private provider details", Text(result));
    }

    [Fact]
    public async Task StdioBridge_InitializesAndForwardsAuthenticatedTools()
    {
        await using var app = await StartAsync(network: true);
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var bridge = Path.Combine(root, "src/AgentRag.McpBridge/bin", configuration, "net10.0/AgentRag.McpBridge.dll");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet", Arguments = [bridge],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["AGENTRAG_MCP_URL"] = app.Urls.Single() + "/mcp",
                ["AGENTRAG_MCP_TOKEN"] = Token(app, "alice")
            }
        }), cancellationToken: timeout.Token);
        Assert.Equal(6, (await client.ListToolsAsync(cancellationToken: timeout.Token)).Count);
        var result = Text(await client.CallToolAsync("list_documents", cancellationToken: timeout.Token));
        Assert.Contains("Alice guide", result);
        Assert.DoesNotContain("Bob secret", result);
    }

    private static async Task<WebApplication> StartAsync(bool network = false)
    {
        var builder = WebApplication.CreateBuilder();
        if (network) builder.WebHost.UseUrls("http://127.0.0.1:0");
        else builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(IdentityConstants.BearerScheme).AddBearerToken(IdentityConstants.BearerScheme);
        builder.Services.AddAuthorization();
        // Share one database across the request scopes in this host.
        var name = Guid.NewGuid().ToString();
        builder.Services.AddScoped<AppDbContext>(_ => new McpDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options));
        builder.Services.AddSingleton<IChatClient>(new AnswerClient());
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FailingEmbeddings());
        builder.Services.AddOptions<EmbeddingOptions>();
        builder.Services.AddSingleton<EmbeddingService>();
        builder.Services.AddSingleton<RagAgentFactory>();
        builder.Services.AddScoped<RagService>();
        builder.Services.AddRagMcp();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRagMcp();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Documents.AddRange(new KnowledgeDocument { OwnerId = "alice", Title = "Alice guide" },
                new KnowledgeDocument { OwnerId = "bob", Title = "Bob secret" });
            await db.SaveChangesAsync();
        }
        await app.StartAsync();
        return app;
    }

    private static string Token(WebApplication app, string owner, bool expired = false)
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>().Get(IdentityConstants.BearerScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], IdentityConstants.BearerScheme));
        return options.BearerTokenProtector.Protect(new AuthenticationTicket(principal,
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 30) }, IdentityConstants.BearerScheme));
    }

    private static Task<McpClient> ClientAsync(HttpClient http, string token) => McpClient.CreateAsync(
        new HttpClientTransport(new() { Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token } }, http));

    private static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private sealed class AnswerClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "A grounded answer [Alice guide]")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class McpDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            // These protocol tests don't execute vector SQL; PostgreSQL translation is tested separately.
            builder.Entity<DocumentChunk>().Ignore(c => c.Embedding);
        }
    }

    private sealed class FailingEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("private provider details");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
