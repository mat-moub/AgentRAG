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
        Assert.Equal(new[] { "ask_rag", "list_documents", "search_knowledge_base" }, tools.Select(t => t.Name).Order().ToArray());
        Assert.All(tools, t => Assert.DoesNotContain("userId", t.JsonSchema.GetRawText()));
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
    public async Task Tools_InvalidArguments_ReturnToolErrors(string name, string key, object value)
    {
        await using var app = await StartAsync();
        using var http = app.GetTestClient();
        await using var client = await ClientAsync(http, Token(app, "alice"));
        var arguments = new Dictionary<string, object?> { [key] = value };
        if (key == "top") arguments["query"] = "query";
        var result = await client.CallToolAsync(name, arguments);
        Assert.True(result.IsError);
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
        Assert.Equal(3, (await client.ListToolsAsync(cancellationToken: timeout.Token)).Count);
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
