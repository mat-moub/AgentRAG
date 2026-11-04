using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector.EntityFrameworkCore;
#pragma warning disable OPENAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using OpenAI.Responses;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(connectionString, postgres => postgres.UseVector()));

builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<ApplicationUser>()
    .AddEntityFrameworkStores<AppDbContext>();

var modelEndpoint = new Uri(builder.Configuration["OpenAI:Endpoint"] ?? "https://api.openai.com/v1");
var modelTimeoutSeconds = builder.Configuration.GetValue<int>("OpenAI:NetworkTimeoutSeconds", 1800);
if (modelTimeoutSeconds is < 1 or > 86400)
    throw new InvalidOperationException("OpenAI:NetworkTimeoutSeconds must be between 1 and 86400.");
var modelTimeout = TimeSpan.FromSeconds(modelTimeoutSeconds);
var openAiKey = new ApiKeyCredential(
    builder.Configuration["OpenAI:ApiKey"] is { Length: > 0 } key ? key : "not-configured");
// The SDK owns each network-operation timeout; HttpClient must not add a shorter deadline.
builder.Services.AddHttpClient("OpenAI", client => client.Timeout = Timeout.InfiniteTimeSpan);
// Ingestion owns retries so the UI sees quota waits rather than hidden SDK retries.
// Azure AI Foundry exposes the Responses API under /openai/v1
builder.Services.AddSingleton<IChatClient>(services =>
    new ResponsesClient(openAiKey, new ResponsesClientOptions
    {
        Endpoint = modelEndpoint,
        NetworkTimeout = modelTimeout,
        Transport = new HttpClientPipelineTransport(services.GetRequiredService<IHttpClientFactory>().CreateClient("OpenAI"))
    })
        .AsIChatClient(builder.Configuration["OpenAI:ChatModel"] ?? "gpt-4o-mini"));
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(services =>
    new OpenAIClient(openAiKey, new OpenAIClientOptions
    {
        Endpoint = modelEndpoint,
        NetworkTimeout = modelTimeout,
        Transport = new HttpClientPipelineTransport(services.GetRequiredService<IHttpClientFactory>().CreateClient("OpenAI")),
        RetryPolicy = new ClientRetryPolicy(0)
    }).GetEmbeddingClient(builder.Configuration["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small").AsIEmbeddingGenerator());
builder.Services.AddSingleton<RagAgentFactory>();
builder.Services.AddOptions<EmbeddingOptions>()
    .Bind(builder.Configuration.GetSection("Ingestion"))
    .Validate(o => o.BatchSize is >= 1 and <= 128, "Ingestion:BatchSize must be between 1 and 128.")
    .Validate(o => o.MaxRetries is >= 0 and <= 10, "Ingestion:MaxRetries must be between 0 and 10.")
    .Validate(o => o.SaveCommandTimeoutSeconds is >= 1 and <= 86400, "Ingestion:SaveCommandTimeoutSeconds must be between 1 and 86400.")
    .ValidateOnStart();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddScoped<RagService>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
     .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapGroup("/api/auth").MapIdentityApi<ApplicationUser>();
app.MapControllers();

app.Run();

public partial class Program;
