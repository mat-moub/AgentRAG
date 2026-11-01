using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
#pragma warning disable OPENAI001
using System.ClientModel;
using OpenAI;
using OpenAI.Responses;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(connectionString));

builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<ApplicationUser>()
    .AddEntityFrameworkStores<AppDbContext>();

var openAiOptions = new OpenAIClientOptions
{
    Endpoint = new Uri(builder.Configuration["OpenAI:Endpoint"] ?? "https://api.openai.com/v1")
};
var openAiKey = new ApiKeyCredential(
    builder.Configuration["OpenAI:ApiKey"] is { Length: > 0 } key ? key : "not-configured");
var openAi = new OpenAIClient(openAiKey, openAiOptions);
// Azure AI Foundry exposes the Responses API under /openai/v1
builder.Services.AddSingleton<IChatClient>(
    new ResponsesClient(openAiKey, new ResponsesClientOptions { Endpoint = openAiOptions.Endpoint })
        .AsIChatClient(builder.Configuration["OpenAI:ChatModel"] ?? "gpt-4o-mini"));
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    openAi.GetEmbeddingClient(builder.Configuration["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small").AsIEmbeddingGenerator());
builder.Services.AddSingleton<RagAgentFactory>();
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
