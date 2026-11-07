using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Claims;
using AgentRag.Api.Auth;
using AgentRag.Api.Controllers;
using AgentRag.Api.Data;
using AgentRag.Api.Mcp;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenIddict.Abstractions;
using OpenIddict.Server;

public class OAuthTests
{
    private const string Resource = "http://127.0.0.1:5000/mcp";
    private const string Password = "Research1!";

    [Theory]
    [InlineData("https://attacker.example/callback")]
    [InlineData("http://localhost.attacker.example/callback")]
    [InlineData("http://127.0.0.1/callback#fragment")]
    [InlineData("http://user@127.0.0.1/callback")]
    public void Registration_RejectsUnsafeRedirects(string redirect)
    {
        var metadata = JsonSerializer.SerializeToElement(new { redirect_uris = new[] { redirect } });
        Assert.False(OAuthController.TryClientMetadata(metadata, out _, out _));
    }

    [Fact]
    public async Task Discovery_AdvertisesOAuthAndCookieRequestsRequireCsrf()
    {
        await using var host = await Host.StartAsync();
        var resource = await host.Browser.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        Assert.Equal(Resource, resource.GetProperty("resource").GetString());
        var metadata = await host.Browser.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        Assert.EndsWith("/connect/register", metadata.GetProperty("registration_endpoint").GetString());
        var anonymous = await host.Browser.PostAsync("/mcp", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("resource_metadata", anonymous.Headers.WwwAuthenticate.ToString());
        await host.LoginAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Browser.PostAsync("/api/auth/logout", null)).StatusCode);
        var session = await host.Browser.GetFromJsonAsync<JsonElement>("/api/auth/session");
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await host.Browser.SendAsync(logout)).StatusCode);
    }

    [Fact]
    public async Task Authorization_RotatesRefreshTokensAndRevokesAccess()
    {
        await using var host = await Host.StartAsync();
        await host.LoginAsync();
        var clientId = await host.RegisterAsync();
        var code = await host.CodeAsync(clientId);
        var tokens = await host.ExchangeAsync(clientId, code);
        Assert.InRange(tokens.GetProperty("expires_in").GetInt32(), 3595, 3600);
        var access = tokens.GetProperty("access_token").GetString()!;
        using var http = host.App.GetTestClient();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(Resource), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + access }
        }, http));
        var documents = await mcp.CallToolAsync("list_documents");
        var text = string.Join("", documents.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("Alice guide", text);
        Assert.DoesNotContain("Bob secret", text);

        var refresh = tokens.GetProperty("refresh_token").GetString()!;
        var refreshed = await host.TokenAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refresh, ["resource"] = Resource });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var next = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(refresh, next.GetProperty("refresh_token").GetString());
        var newAccess = next.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await host.Browser.PostAsync("/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["token"] = newAccess, ["token_type_hint"] = "access_token"
        }))).StatusCode);
        using var revoked = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        revoked.Headers.Authorization = new("Bearer", newAccess);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Browser.SendAsync(revoked)).StatusCode);
        var replay = await host.TokenAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refresh });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Authorization_RejectsWrongPkceReusedCodeAndWrongResource()
    {
        await using var host = await Host.StartAsync();
        await host.LoginAsync();
        var clientId = await host.RegisterAsync();
        var badTarget = await host.Browser.GetAsync(host.AuthorizeUrl(clientId).Replace(Uri.EscapeDataString(Resource), Uri.EscapeDataString("https://foreign.example/mcp")));
        Assert.NotEqual(HttpStatusCode.OK, badTarget.StatusCode);
        var code = await host.CodeAsync(clientId);
        var wrong = await host.TokenAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId,
            ["code"] = code, ["redirect_uri"] = "http://127.0.0.1:34567/callback", ["code_verifier"] = new string('b', 64) });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        code = await host.CodeAsync(clientId);
        await host.ExchangeAsync(clientId, code);
        var replay = await host.TokenAsync(Host.CodeRequest(clientId, code));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        var deny = await host.Browser.GetAsync(host.AuthorizeUrl(clientId));
        var denied = await host.FormAsync(host.AuthorizeUrl(clientId), deny, new() { ["decision"] = "deny" });
        Assert.Contains("access_denied", denied.Headers.Location?.Query);
        var injected = await host.Browser.GetAsync(host.AuthorizeUrl(clientId) + "&decision=allow");
        Assert.DoesNotContain("type=\"hidden\" name=\"decision\"", await injected.Content.ReadAsStringAsync());
        var stillDenied = await host.FormAsync(host.AuthorizeUrl(clientId), injected, new() { ["decision"] = "deny" });
        Assert.Contains("access_denied", stillDenied.Headers.Location?.Query);
    }

    [Fact]
    public async Task Google_RequiresExplicitLinkAndPreservesExistingOwner()
    {
        await using var host = await Host.StartAsync();
        var callback = await host.GoogleAsync();
        Assert.StartsWith("/account/google/new", callback.Headers.Location?.OriginalString);
        var choice = await host.Browser.GetAsync(callback.Headers.Location);
        var collision = await host.FormAsync("/account/google/new", choice, new() { ["decision"] = "create" });
        Assert.Contains("already uses this email", await collision.Content.ReadAsStringAsync());
        var linked = await host.FormAsync("/account/google/new", collision, new()
        {
            ["decision"] = "link", ["email"] = "alice@example.com", ["password"] = Password
        });
        Assert.Equal(HttpStatusCode.Redirect, linked.StatusCode);
        using var scope = host.App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await users.FindByLoginAsync("Google", "google-alice");
        Assert.Equal(host.AliceId, owner?.Id);
        Assert.Equal(host.AliceId, (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Documents.SingleAsync(d => d.Title == "Alice guide")).OwnerId);
        await host.Browser.PostAsync("/api/auth/logout", null); // CSRF rejection must keep session intact.
        var known = await host.GoogleAsync();
        Assert.Equal(new Uri("http://127.0.0.1:5173/"), known.Headers.Location);
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("scope")]
    [InlineData("expired")]
    public async Task Mcp_RejectsOAuthTokensWithWrongAudienceMissingScopeOrExpiredLifetime(string fault)
    {
        await using var host = await Host.StartAsync(fault);
        await host.LoginAsync();
        var clientId = await host.RegisterAsync();
        var tokens = await host.ExchangeAsync(clientId, await host.CodeAsync(clientId));
        using var message = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        message.Headers.Authorization = new("Bearer", tokens.GetProperty("access_token").GetString());
        if (fault == "expired") await Task.Delay(1500);
        var result = await host.Browser.SendAsync(message);
        Assert.Equal(fault == "scope" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task Linking_RequiresPasswordAndRejectsGoogleAccountAlreadyOwnedByAnotherUser()
    {
        await using var host = await Host.StartAsync();
        using (var scope = host.App.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var bob = new ApplicationUser { UserName = "bob@example.com", Email = "bob@example.com" };
            Assert.True((await users.CreateAsync(bob, Password)).Succeeded);
            Assert.True((await users.AddLoginAsync(bob, new UserLoginInfo("Google", "google-alice", "Google"))).Succeeded);
        }
        var page = await host.Browser.GetAsync("/account/link");
        var wrong = await host.FormAsync("/account/link", page, new() { ["email"] = "alice@example.com", ["password"] = "incorrect" });
        Assert.Contains("Check your AgentRAG credentials", await wrong.Content.ReadAsStringAsync());
        var challenge = await host.FormAsync("/account/link", wrong, new() { ["email"] = "alice@example.com", ["password"] = Password });
        var rejected = await host.CompleteGoogleAsync(challenge);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var check = host.App.Services.CreateScope();
        Assert.Empty(await check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .GetLoginsAsync((await check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(host.AliceId))!));
    }

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Browser { get; init; }
        public required string AliceId { get; init; }
        public static async Task<Host> StartAsync(string? tokenFault = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development", ApplicationName = typeof(Program).Assembly.GetName().Name
            });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Google:ClientId"] = "test-google", ["Authentication:Google:ClientSecret"] = "test-secret"
            });
            var name = Guid.NewGuid().ToString();
            builder.Services.AddScoped<AppDbContext>(_ => new TestDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options));
            builder.Services.AddAuthorization();
            builder.Services.AddIdentityApiEndpoints<ApplicationUser>().AddEntityFrameworkStores<AppDbContext>();
            builder.Services.AddAgentRagOAuth(builder.Configuration, builder.Environment, o => o.AddEphemeralEncryptionKey().AddEphemeralSigningKey());
            builder.Services.AddOpenIddict().AddValidation(o => o.Configure(options => options.TokenValidationParameters.ClockSkew = TimeSpan.Zero));
            if (tokenFault is not null)
                builder.Services.AddOpenIddict().AddServer(o => o.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(h =>
                    h.SetOrder(OpenIddictServerHandlers.InferResources.Descriptor.Order + 500).UseInlineHandler(c =>
                    {
                        if (c.EndpointType == OpenIddictServerEndpointType.Token)
                        {
                            if (tokenFault == "audience") c.Principal!.SetResources("https://foreign.example/mcp");
                            if (tokenFault == "scope") c.Principal!.SetScopes(Array.Empty<string>());
                            if (tokenFault == "expired") c.Principal!.SetAccessTokenLifetime(TimeSpan.FromSeconds(1));
                        }
                        return default;
                    })));
            builder.Services.Configure<GoogleOptions>("Google", o => o.Backchannel = new HttpClient(new GoogleBackchannel()));
            builder.Services.AddControllersWithViews().AddApplicationPart(typeof(OAuthController).Assembly);
            builder.Services.AddScoped<RagService>();
            builder.Services.AddSingleton<IChatClient>(new NoModels());
            builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new NoModels());
            builder.Services.AddOptions<EmbeddingOptions>();
            builder.Services.AddSingleton<EmbeddingService>();
            builder.Services.AddSingleton<RagAgentFactory>();
            builder.Services.AddRagMcp();
            var app = builder.Build();
            app.UseRouting();
            app.UseAgentRagCookieProtection();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapRagMcp();
            await app.StartAsync();
            using var scope = app.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var alice = new ApplicationUser { UserName = "alice@example.com", Email = "alice@example.com" };
            Assert.True((await users.CreateAsync(alice, Password)).Succeeded);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Documents.AddRange(new KnowledgeDocument { OwnerId = alice.Id, Title = "Alice guide" },
                new KnowledgeDocument { OwnerId = "bob", Title = "Bob secret" });
            await db.SaveChangesAsync();
            var browser = new HttpClient(new Cookies(app.GetTestServer().CreateHandler())) { BaseAddress = new Uri("http://127.0.0.1:5000") };
            return new Host { App = app, Browser = browser, AliceId = alice.Id };
        }

        public async Task LoginAsync()
        {
            var login = await Browser.GetAsync("/account/login");
            var response = await FormAsync("/account/login", login, new() { ["email"] = "alice@example.com", ["password"] = Password });
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
        public async Task<string> RegisterAsync()
        {
            var response = await Browser.PostAsJsonAsync("/connect/register", new
            {
                client_name = "Test Codex", redirect_uris = new[] { "http://127.0.0.1:34567/callback" }, token_endpoint_auth_method = "none"
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
        }
        public string AuthorizeUrl(string clientId) => "/connect/authorize?" + new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = "http://127.0.0.1:34567/callback",
            ["scope"] = "agentrag.read offline_access", ["state"] = "state", ["resource"] = Resource,
            ["code_challenge_method"] = "S256", ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(new string('a', 64))))
        }).ReadAsStringAsync().GetAwaiter().GetResult();
        public async Task<string> CodeAsync(string clientId)
        {
            var url = AuthorizeUrl(clientId);
            var consent = await Browser.GetAsync(url);
            Assert.True(consent.StatusCode == HttpStatusCode.OK, await consent.Content.ReadAsStringAsync());
            var result = await FormAsync(url, consent, new() { ["decision"] = "allow" });
            Assert.True(result.StatusCode == HttpStatusCode.Redirect, await result.Content.ReadAsStringAsync());
            return QueryHelpers.ParseQuery(result.Headers.Location!.Query)["code"]!;
        }
        public static Dictionary<string, string> CodeRequest(string clientId, string code) => new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
            ["redirect_uri"] = "http://127.0.0.1:34567/callback", ["code_verifier"] = new string('a', 64), ["resource"] = Resource
        };
        public Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form) => Browser.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        public async Task<JsonElement> ExchangeAsync(string clientId, string code)
        {
            var response = await TokenAsync(CodeRequest(clientId, code));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async Task<HttpResponseMessage> FormAsync(string url, HttpResponseMessage page, Dictionary<string, string> form)
        {
            var html = await page.Content.ReadAsStringAsync();
            foreach (Match hidden in Regex.Matches(html, "type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\""))
                form.TryAdd(WebUtility.HtmlDecode(hidden.Groups[1].Value), WebUtility.HtmlDecode(hidden.Groups[2].Value));
            var csrf = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(csrf.Success, html);
            form["__RequestVerificationToken"] = WebUtility.HtmlDecode(csrf.Groups[1].Value);
            return await Browser.PostAsync(url, new FormUrlEncodedContent(form));
        }
        public async Task<HttpResponseMessage> GoogleAsync()
        {
            var challenge = await Browser.GetAsync("/account/google/start");
            return await CompleteGoogleAsync(challenge);
        }
        public async Task<HttpResponseMessage> CompleteGoogleAsync(HttpResponseMessage challenge)
        {
            var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
            var callback = await Browser.GetAsync("/signin-google?code=google-code&state=" + Uri.EscapeDataString(state!));
            return await Browser.GetAsync(callback.Headers.Location);
        }
        public async ValueTask DisposeAsync() { Browser.Dispose(); await App.DisposeAsync(); }
    }

    private sealed class Cookies(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly Dictionary<string, string> cookies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => c.Key + "=" + c.Value)));
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values)
                {
                    var pair = value.Split(';')[0].Split('=', 2);
                    cookies[pair[0]] = pair[1];
                }
            return response;
        }
    }

    private sealed class GoogleBackchannel : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(request.RequestUri!.AbsolutePath.EndsWith("/token")
                    ? (object)new { access_token = "google-token", token_type = "Bearer", expires_in = 3600 }
                    : new { id = "google-alice", email = "alice@example.com", name = "Alice", verified_email = true })
            });
    }

    private sealed class TestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<DocumentChunk>().Ignore(c => c.Embedding);
        }
    }

    private sealed class NoModels : IChatClient, IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
