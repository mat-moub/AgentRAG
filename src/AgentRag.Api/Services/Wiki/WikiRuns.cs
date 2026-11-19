using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentRag.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentRag.Api.Services;

/// <summary>Wakes the worker when a run is queued and lets a request cancel the run in progress.</summary>
public sealed class WikiRunQueue
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentDictionary<int, CancellationTokenSource> running = new();

    public void Signal() => wake.Writer.TryWrite(true);

    /// <summary>Returns when a run was queued, or after the delay: the table is the queue, the signal only avoids waiting.</summary>
    public async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(delay);
        try { await wake.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    public CancellationTokenSource Begin(int runId, CancellationToken stopping)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        running[runId] = source;
        return source;
    }

    public void End(int runId) => running.TryRemove(runId, out _);

    public bool Cancel(int runId)
    {
        if (!running.TryGetValue(runId, out var source)) return false;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }
}

/// <summary>
/// Executes wiki runs outside HTTP requests, one at a time. A build can take longer than any request and must
/// survive a closed browser tab; it also survives a restart, because every stage saves what it finished.
/// </summary>
public sealed class WikiRunWorker(IServiceScopeFactory scopes, WikiRunQueue queue, ILogger<WikiRunWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var recovered = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Runs interrupted by a restart or an application-pool recycle go back to the queue once.
                if (!recovered) recovered = await RecoverAsync(stoppingToken);
                if (await NextAsync(stoppingToken) is { } runId) await RunAsync(runId, stoppingToken);
                else await queue.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "The wiki worker could not read its queue");
                await queue.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task<bool> RecoverAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var interrupted = await db.WikiRuns.Where(r => r.Status == WikiRunStatus.Running).ToListAsync(ct);
        foreach (var run in interrupted)
            (run.Status, run.Stage) = (WikiRunStatus.Queued, "queued");
        await db.SaveChangesAsync(ct);
        if (interrupted.Count > 0) logger.LogInformation("Requeued {Count} interrupted wiki runs", interrupted.Count);
        return true;
    }

    private async Task<int?> NextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WikiRuns.Where(r => r.Status == WikiRunStatus.Queued).OrderBy(r => r.Id).Select(r => (int?)r.Id).FirstOrDefaultAsync(ct);
    }

    internal async Task RunAsync(int runId, CancellationToken stopping)
    {
        using var cancellation = queue.Begin(runId, stopping);
        string status = WikiRunStatus.Completed;
        string? error = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WikiBuilder>().RunAsync(runId, cancellation.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The application is stopping: the run stays "running" and is requeued at the next start.
            return;
        }
        catch (OperationCanceledException) { status = WikiRunStatus.Cancelled; }
        catch (Exception ex)
        {
            status = WikiRunStatus.Failed;
            error = ex is LibraryException ? ex.Message : "The wiki refresh failed. What was already built is kept; refresh again to continue.";
            if (ex is not LibraryException) logger.LogError(ex, "Wiki run {RunId} failed", runId);
        }
        finally { queue.End(runId); }

        // A fresh context: the builder's one may hold the changes that failed to save.
        await using var closing = scopes.CreateAsyncScope();
        var db = closing.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.WikiRuns.SingleOrDefaultAsync(r => r.Id == runId, CancellationToken.None) is not { } run) return;
        (run.Status, run.Error, run.FinishedUtc) = (status, error, DateTime.UtcNow);
        if (status != WikiRunStatus.Completed) run.Stage = status;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
