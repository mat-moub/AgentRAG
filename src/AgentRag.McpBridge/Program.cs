using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

// This process never opens the database. All tools go through the authenticated HTTP server.
var endpoint = Environment.GetEnvironmentVariable("AGENTRAG_MCP_URL") ?? "http://localhost:5000/mcp";
var token = Environment.GetEnvironmentVariable("AGENTRAG_MCP_TOKEN");
if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
    || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
    throw new InvalidOperationException("AGENTRAG_MCP_URL must use HTTPS (HTTP is allowed only on loopback).");
if (string.IsNullOrWhiteSpace(token))
    throw new InvalidOperationException("Set AGENTRAG_MCP_TOKEN to an AgentRAG access token before starting the bridge.");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
await using var remote = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = uri,
    TransportMode = HttpTransportMode.StreamableHttp,
    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
}, http));
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = remote.ServerInfo;
    options.ServerInstructions = remote.ServerInstructions;
}).WithStdioServerTransport()
    .WithListToolsHandler(async (_, ct) => new() { Tools = (await remote.ListToolsAsync(cancellationToken: ct)).Select(tool => tool.ProtocolTool).ToList() })
    .WithCallToolHandler((request, ct) => remote.CallToolAsync(request.Params!, ct));
await builder.Build().RunAsync();
