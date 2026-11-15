using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.EntityFrameworkCore;

public class WikiRunWorkerTests
{
    private static async Task<int> QueueRunAsync(WikiLibrary library, string user = WikiLibrary.Owner)
    {
        await using var db = library.Open();
        return (await library.Service(db).StartRunAsync(user, library.WikiId, null, default)).Id;
    }

    private static async Task<WikiRun> RunAsync(WikiLibrary library, int id)
    {
        await using var db = library.Open();
        return await db.WikiRuns.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [Fact]
    public async Task Run_QueuedRun_IsCompletedWithStats()
    {
        using var library = new WikiLibrary();
        var id = await QueueRunAsync(library);

        await library.Worker.RunAsync(id, default);

        var run = await RunAsync(library, id);
        Assert.Equal((WikiRunStatus.Completed, "completed", (string?)null), (run.Status, run.Stage, run.Error));
        Assert.NotNull(run.StartedUtc);
        Assert.NotNull(run.FinishedUtc);
        Assert.Contains("\"pages\":", run.Stats);
        Assert.False(WikiRunStatus.IsActive(run.Status));
    }

    [Fact]
    public async Task Run_EveryExtractionThrows_FailsWithoutLeakingTheExceptionText()
    {
        using var library = new WikiLibrary();
        library.Model.FailEveryExtraction = "401 Unauthorized: key sk-secret-1234 rejected by https://internal.example";
        var id = await QueueRunAsync(library);

        await library.Worker.RunAsync(id, default);

        var run = await RunAsync(library, id);
        Assert.Equal((WikiRunStatus.Failed, "failed"), (run.Status, run.Stage));
        Assert.NotNull(run.FinishedUtc);
        Assert.False(string.IsNullOrWhiteSpace(run.Error));
        Assert.DoesNotContain("sk-secret", run.Error);
        Assert.DoesNotContain("internal.example", run.Error);
        Assert.DoesNotContain("Unauthorized", run.Error);
        await using var db = library.Open();
        Assert.Equal(0, await db.WikiPages.CountAsync());
        // A failed run no longer blocks the next one.
        Assert.Equal(WikiRunStatus.Queued, (await RunAsync(library, await QueueRunAsync(library))).Status);
    }

    [Fact]
    public async Task Run_AfterAFailure_ResumesAndCompletesOnceTheModelWorks()
    {
        using var library = new WikiLibrary();
        library.Model.FailEveryExtraction = "boom";
        await library.Worker.RunAsync(await QueueRunAsync(library), default);
        library.Model.FailEveryExtraction = null;

        var id = await QueueRunAsync(library);
        await library.Worker.RunAsync(id, default);

        Assert.Equal(WikiRunStatus.Completed, (await RunAsync(library, id)).Status);
        await using var db = library.Open();
        Assert.True(await db.WikiPages.AnyAsync(p => p.Slug == "order-checkout"));
    }

    [Fact]
    public async Task Run_RequesterLostWriteAccess_FailsWithTheAccessMessageAndPublishesNothing()
    {
        using var library = new WikiLibrary();
        var id = await QueueRunAsync(library, WikiLibrary.Writer);
        await using (var db = library.Open())
        {
            db.FolderMembers.Remove(await db.FolderMembers.SingleAsync(m => m.FolderId == library.ParentId && m.UserId == WikiLibrary.Writer));
            await db.SaveChangesAsync();
        }

        await library.Worker.RunAsync(id, default);

        var run = await RunAsync(library, id);
        Assert.Equal(WikiRunStatus.Failed, run.Status);
        Assert.Contains("unavailable", run.Error);
        await using var read = library.Open();
        Assert.Equal(0, await read.WikiPages.CountAsync());
        Assert.Equal(0, library.Model.Total);
    }

    [Fact]
    public async Task Run_CancelledWhileRunning_EndsCancelledRatherThanFailed()
    {
        using var library = new WikiLibrary();
        var id = await QueueRunAsync(library);
        library.Model.OnExtract = ct =>
        {
            Assert.True(library.Queue.Cancel(id));
            ct.ThrowIfCancellationRequested();
        };

        await library.Worker.RunAsync(id, default);

        var run = await RunAsync(library, id);
        Assert.Equal((WikiRunStatus.Cancelled, "cancelled", (string?)null), (run.Status, run.Stage, run.Error));
        Assert.NotNull(run.FinishedUtc);
    }

    [Fact]
    public async Task Run_ApplicationStopping_LeavesTheRunRunningSoItIsRequeuedAtNextStart()
    {
        using var library = new WikiLibrary();
        var id = await QueueRunAsync(library);
        using var stopping = new CancellationTokenSource();
        library.Model.OnExtract = ct =>
        {
            stopping.Cancel();
            ct.ThrowIfCancellationRequested();
        };

        await library.Worker.RunAsync(id, stopping.Token);

        var run = await RunAsync(library, id);
        Assert.Equal(WikiRunStatus.Running, run.Status);
        Assert.Null(run.FinishedUtc);
    }

    [Fact]
    public async Task Cancel_QueuedRun_IsMarkedCancelledAndAllowsANewRun()
    {
        using var library = new WikiLibrary();
        var id = await QueueRunAsync(library);
        await using var db = library.Open();
        var service = library.Service(db);

        var cancelled = await service.CancelRunAsync(WikiLibrary.Owner, library.WikiId, id, default);

        Assert.Equal((WikiRunStatus.Cancelled, "cancelled"), (cancelled.Status, cancelled.Stage));
        Assert.Equal(WikiRunStatus.Queued, (await service.StartRunAsync(WikiLibrary.Owner, library.WikiId, null, default)).Status);
    }
}
