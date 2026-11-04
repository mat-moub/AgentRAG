using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AgentRag.Api.Services;

public sealed class EmbeddingOptions
{
    public int BatchSize { get; set; } = 16;
    public int MaxRetries { get; set; } = 3;
    public int SaveCommandTimeoutSeconds { get; set; } = 600;
}

public record EmbeddingRetry(DateTimeOffset RetryAt, int Attempt);

// Shared by all requests in this API process, including retrieval.
public sealed class EmbeddingService(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<EmbeddingOptions> options,
    ILogger<EmbeddingService> logger) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public int BatchSize => options.Value.BatchSize;

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IReadOnlyList<string> inputs, Action<EmbeddingRetry>? onRetry = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                try
                {
                    var result = await generator.GenerateAsync(inputs, cancellationToken: ct);
                    if (result.Count != inputs.Count)
                        throw new InvalidOperationException("The embedding provider returned an unexpected vector count.");
                    logger.LogInformation("Generated {Count} embeddings in {ElapsedMs} ms", inputs.Count, watch.ElapsedMilliseconds);
                    return result;
                }
                catch (ClientResultException ex) when (ex.Status == 429 && IsRateLimit(ex) && attempt < options.Value.MaxRetries)
                {
                    var headers = ex.GetRawResponse()?.Headers;
                    string? milliseconds = null, retryAfter = null;
                    headers?.TryGetValue("retry-after-ms", out milliseconds);
                    if (milliseconds is null) headers?.TryGetValue("x-ms-retry-after-ms", out milliseconds);
                    headers?.TryGetValue("Retry-After", out retryAfter);
                    var now = DateTimeOffset.UtcNow;
                    var delay = RetryDelay(milliseconds, retryAfter, attempt, now);
                    logger.LogWarning("Embedding rate limit (HTTP 429). Retry {Attempt}/{MaxRetries} in {DelaySeconds} seconds for {Count} inputs",
                        attempt + 1, options.Value.MaxRetries, delay.TotalSeconds, inputs.Count);
                    onRetry?.Invoke(new EmbeddingRetry(now + delay, attempt + 1));
                    await Task.Delay(delay, ct);
                }
            }
        }
        finally { gate.Release(); }
    }

    private static bool IsRateLimit(ClientResultException ex) =>
        !ex.Message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) &&
        !ex.Message.Contains("billing_hard_limit", StringComparison.OrdinalIgnoreCase);

    internal static TimeSpan RetryDelay(string? milliseconds, string? retryAfter, int attempt, DateTimeOffset now)
    {
        if (double.TryParse(milliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && double.IsFinite(ms) && ms > 0 && ms <= TimeSpan.FromDays(1).TotalMilliseconds)
            return TimeSpan.FromMilliseconds(ms);
        if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds > 0 && seconds <= 86400)
            return TimeSpan.FromSeconds(seconds);
        if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) && date > now && date - now <= TimeSpan.FromDays(1))
            return date - now;
        // Azure can ask for a full minute. Never immediately hammer a saturated deployment.
        return TimeSpan.FromSeconds(Math.Min(60 * Math.Pow(2, attempt), 300) + Random.Shared.NextDouble());
    }

    public void Dispose() => gate.Dispose();
}
