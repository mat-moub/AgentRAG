using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AgentRag.Api.Controllers;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using Pgvector.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using System.Threading.Channels;

public class IngestionTests
{
    [Fact]
    public async Task Upload_ExtractsFile_StreamsIngestionForCurrentOwner()
    {
        using var generator = new RecordingGenerator();
        using var service = Service(generator);
        using var db = new RecordingDbContext();
        var controller = Controller(db, service);
        var bytes = Encoding.UTF8.GetBytes("Text extracted from the uploaded document.");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "guide.txt");
        await controller.Upload(file, null, new DocumentTextExtractor(), CancellationToken.None);
        Assert.Equal("completed", ReadEvents(controller)[^1].Type);
        var saved = db.ChangeTracker.Entries<KnowledgeDocument>().Single().Entity;
        Assert.Equal("guide", saved.Title);
        Assert.Equal("owner", saved.OwnerId);
        Assert.Equal("Text extracted from the uploaded document.", Assert.Single(saved.Chunks).Content);
    }

    [Fact]
    public async Task Upload_InvalidPdf_ReturnsBadRequestWithoutSaving()
    {
        using var generator = new RecordingGenerator();
        using var service = Service(generator);
        using var db = new RecordingDbContext();
        var controller = Controller(db, service);
        var file = new FormFile(new MemoryStream("broken"u8.ToArray()), 0, 6, "file", "broken.pdf");
        await controller.Upload(file, "Guide", new DocumentTextExtractor(), CancellationToken.None);
        Assert.Equal(400, controller.Response.StatusCode);
        Assert.Equal(0, db.Saves);
        Assert.Empty(generator.BatchSizes);
    }

    [Theory]
    [InlineData("1500", "60", 1.5)]
    [InlineData(null, "60", 60)]
    [InlineData("invalid", "60", 60)]
    [InlineData(null, "Wed, 04 Nov 2026 12:01:00 GMT", 60)]
    public void RetryDelay_RespectsProviderHeaders(string? milliseconds, string retryAfter, double seconds)
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(seconds, EmbeddingService.RetryDelay(milliseconds, retryAfter, 0, now).TotalSeconds);
    }

    [Fact]
    public void RetryDelay_MissingHeaders_BacksOffFromOneMinute()
    {
        Assert.InRange(EmbeddingService.RetryDelay(null, null, 0, DateTimeOffset.UtcNow).TotalSeconds, 60, 61);
        Assert.InRange(EmbeddingService.RetryDelay(null, "invalid", 1, DateTimeOffset.UtcNow).TotalSeconds, 120, 121);
    }

    [Fact]
    public async Task GenerateAsync_RateLimited_RetriesAndReportsWait()
    {
        using var handler = new EmbeddingHandler(call => call == 1 ? RateLimited() : Success());
        using var generator = CreateGenerator(handler);
        using var service = Service(generator);
        var retries = new List<EmbeddingRetry>();

        var result = await service.GenerateAsync(["text"], retries.Add);

        Assert.Single(result);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, Assert.Single(retries).Attempt);
    }

    [Fact]
    public async Task GenerateAsync_RetriesExhausted_StopsAtConfiguredLimit()
    {
        using var handler = new EmbeddingHandler(_ => RateLimited());
        using var generator = CreateGenerator(handler);
        using var service = Service(generator, retries: 1);
        await Assert.ThrowsAsync<ClientResultException>(() => service.GenerateAsync(["text"]));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(400, "invalid_request")]
    [InlineData(429, "insufficient_quota")]
    public async Task GenerateAsync_PermanentFailure_DoesNotRetry(int status, string code)
    {
        using var handler = new EmbeddingHandler(_ => Failure(status, code));
        using var generator = CreateGenerator(handler);
        using var service = Service(generator);
        await Assert.ThrowsAsync<ClientResultException>(() => service.GenerateAsync(["text"]));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GenerateAsync_CancelDuringQuotaWait_ReleasesSharedGate()
    {
        using var handler = new EmbeddingHandler(call => call == 1 ? RateLimited("60") : Success());
        using var generator = CreateGenerator(handler);
        using var service = Service(generator);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateAsync(["text"], _ => cancellation.Cancel(), cancellation.Token));
        Assert.Single(await service.GenerateAsync(["next"], ct: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task IngestAsync_BatchesInOrder_ReportsProgressAndSavesOnce()
    {
        using var generator = new RecordingGenerator();
        using var service = Service(generator, batchSize: 2);
        using var db = new RecordingDbContext();
        var rag = new RagService(db, service);
        var progress = new List<IngestionProgress>();
        var document = await rag.IngestAsync("owner", "Guide", new string('a', 2400), progress.Add);

        Assert.Equal(new[] { 2, 2 }, generator.BatchSizes);
        Assert.Equal(new[] { 0, 1, 2, 3 }, document.Chunks.Select(c => c.Index));
        Assert.Equal("owner", document.OwnerId);
        Assert.Equal(1, db.Saves);
        Assert.Equal("chunking", progress[0].Stage);
        Assert.Equal("saving", progress[^1].Stage);
        Assert.Equal(4, progress[^1].CompletedChunks);
        Assert.Contains(progress, p => p.Stage == "embedding" && p.CompletedChunks == 2);
    }

    [Fact]
    public async Task IngestAsync_UsesConfiguredSaveTimeoutAndRestoresQueryTimeout()
    {
        using var generator = new RecordingGenerator();
        using var service = Service(generator);
        using var db = new RecordingDbContext();
        db.Database.SetCommandTimeout(47);
        var rag = new RagService(db, service, ingestionOptions: Options.Create(new EmbeddingOptions { SaveCommandTimeoutSeconds = 240 }));
        await rag.IngestAsync("owner", "Guide", "Document text.");
        Assert.Equal(240, db.TimeoutWhenSaving);
        Assert.Equal(47, db.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task IngestAsync_SaveFailure_AlsoRestoresQueryTimeout()
    {
        using var generator = new RecordingGenerator();
        using var service = Service(generator);
        using var db = new RecordingDbContext { FailSave = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RagService(db, service).IngestAsync("owner", "Guide", "Document text."));
        Assert.Equal(600, db.TimeoutWhenSaving);
        Assert.Null(db.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task IngestAsync_FailedLaterBatch_DoesNotSavePartialDocument()
    {
        using var generator = new RecordingGenerator(failAtCall: 2);
        using var service = Service(generator, batchSize: 2);
        using var db = new RecordingDbContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RagService(db, service).IngestAsync("owner", "Guide", new string('a', 2400)));
        Assert.Equal(0, db.Saves);
        Assert.Empty(db.ChangeTracker.Entries<KnowledgeDocument>());
    }

    [Fact]
    public async Task Ingest_StreamsProgressAndTerminalDocument()
    {
        using var generator = new RecordingGenerator();
        using var service = Service(generator);
        using var db = new RecordingDbContext();
        var controller = Controller(db, service);
        await controller.Ingest(new("Guide", "A short document."), CancellationToken.None);
        var events = ReadEvents(controller);
        Assert.Equal("application/x-ndjson", controller.Response.ContentType);
        Assert.Contains(events, e => e.Type == "progress" && e.Progress?.Stage == "saving");
        Assert.Equal("completed", events[^1].Type);
        Assert.Equal("Guide", events[^1].Document!.Title);
        Assert.Equal("owner", db.ChangeTracker.Entries<KnowledgeDocument>().Single().Entity.OwnerId);
    }

    [Fact]
    public async Task Ingest_TwentyTwoMinuteEmbedding_FlushesHeartbeatsAndCompletes()
    {
        var clock = new FakeTimeProvider();
        using var generator = new SlowGenerator(clock);
        using var service = Service(generator);
        using var db = new RecordingDbContext();
        using var body = new HeartbeatStream();
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"));
        context.Response.Body = body;
        var controller = new DocumentsController(db, new RagService(db, service), NullLogger<DocumentsController>.Instance, clock)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        var ingestion = controller.Ingest(new("Long-running guide", "Document text."), CancellationToken.None);
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // More than twenty minutes without embedding progress: the HTTP stream must stay active.
        for (var tick = 0; tick < 121; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await body.Heartbeats.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(ingestion.IsCompleted);
        }

        clock.Advance(TimeSpan.FromSeconds(110));
        await ingestion.WaitAsync(TimeSpan.FromSeconds(5));
        var events = ReadEvents(controller);
        Assert.True(events.Count(e => e.Type == "heartbeat") >= 121);
        Assert.Equal("completed", events[^1].Type);
        Assert.DoesNotContain(events, e => e.Type == "error");
        Assert.Equal(1, db.Saves);
    }

    [Fact]
    public async Task Ingest_AlreadyDisconnected_ReturnsNormallyWithoutSaving()
    {
        using var generator = new RecordingGenerator();
        using var service = Service(generator);
        using var db = new RecordingDbContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var controller = Controller(db, service);
        controller.HttpContext.RequestAborted = cancellation.Token;

        await controller.Ingest(new("Guide", "A short document."), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, db.Saves);
        Assert.DoesNotContain(ReadEvents(controller), e => e.Type is "completed" or "error");
    }

    [Fact]
    public async Task Ingest_ExhaustedRateLimit_StreamsSafeErrorWithoutSaving()
    {
        using var handler = new EmbeddingHandler(_ => RateLimited());
        using var generator = CreateGenerator(handler);
        using var service = Service(generator, retries: 1);
        using var db = new RecordingDbContext();
        var controller = Controller(db, service);
        await controller.Ingest(new("Guide", "A short document."), CancellationToken.None);
        var events = ReadEvents(controller);
        Assert.Contains(events, e => e.Progress?.Stage == "rateLimited");
        Assert.Equal("error", events[^1].Type);
        Assert.Contains("rate-limited", events[^1].Error);
        Assert.DoesNotContain(events, e => e.Type == "completed");
        Assert.Equal(0, db.Saves);
    }

    private static DocumentsController Controller(RecordingDbContext db, EmbeddingService service)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"));
        context.Response.Body = new MemoryStream();
        return new DocumentsController(db, new RagService(db, service), NullLogger<DocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static List<IngestionEvent> ReadEvents(DocumentsController controller) =>
        Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<IngestionEvent>(line, JsonSerializerOptions.Web)!).ToList();

    private static EmbeddingService Service(IEmbeddingGenerator<string, Embedding<float>> generator, int batchSize = 16, int retries = 3) =>
        new(generator, Options.Create(new EmbeddingOptions { BatchSize = batchSize, MaxRetries = retries }), NullLogger<EmbeddingService>.Instance);

    private static IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(HttpMessageHandler handler) =>
        new OpenAIClient(new ApiKeyCredential("unit-test"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://test.invalid/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(0)
        }).GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

    private static HttpResponseMessage Failure(int status, string code) => new((HttpStatusCode)status)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { error = new { message = code, code, type = code } }), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage RateLimited(string? seconds = null)
    {
        var response = Failure(429, "RateLimitReached");
        response.Headers.TryAddWithoutValidation(seconds is null ? "retry-after-ms" : "Retry-After", seconds ?? "1");
        return response;
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"object":"list","model":"text-embedding-3-small","data":[{"object":"embedding","index":0,"embedding":[1,0,0]}],"usage":{"prompt_tokens":1,"total_tokens":1}}""", Encoding.UTF8, "application/json")
    };

    private sealed class EmbeddingHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(++Calls));
    }

    private sealed class RecordingGenerator(int failAtCall = -1) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<int> BatchSizes { get; } = [];
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = values.ToArray();
            BatchSizes.Add(inputs.Length);
            if (BatchSizes.Count == failAtCall) throw new InvalidOperationException("Simulated provider failure");
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(_ => new Embedding<float>(new float[] { 1, 0, 0 }))));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SlowGenerator(TimeProvider clock) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var wait = Task.Delay(TimeSpan.FromMinutes(22), clock, cancellationToken);
            Started.SetResult();
            await wait;
            return new(values.Select(_ => new Embedding<float>(new float[] { 1, 0, 0 })));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HeartbeatStream : MemoryStream
    {
        public Channel<bool> Heartbeats { get; } = Channel.CreateUnbounded<bool>();
        private int pendingHeartbeats;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            pendingHeartbeats += Encoding.UTF8.GetString(buffer.Span).Split('\n')
                .Count(line => line.Contains("\"type\":\"heartbeat\""));
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await base.FlushAsync(cancellationToken);
            while (pendingHeartbeats > 0)
            {
                pendingHeartbeats--;
                Heartbeats.Writer.TryWrite(true);
            }
        }
    }

    private sealed class RecordingDbContext() : AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=localhost;Database=unused", o => o.UseVector()).Options)
    {
        public int Saves { get; private set; }
        public int? TimeoutWhenSaving { get; private set; }
        public bool FailSave { get; init; }
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeoutWhenSaving = Database.GetCommandTimeout();
            if (FailSave) throw new InvalidOperationException("Simulated database failure");
            return Task.FromResult(++Saves);
        }
    }
}
