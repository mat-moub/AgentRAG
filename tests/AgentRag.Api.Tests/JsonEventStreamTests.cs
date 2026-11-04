using AgentRag.Api.Services;
using Microsoft.AspNetCore.Http;

public class JsonEventStreamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_CancelWhileWaiting_StopsProducerAndReturnsNormally(bool requestAborted)
    {
        using var cancellation = new CancellationTokenSource();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        if (requestAborted) context.RequestAborted = cancellation.Token;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var response = JsonEventStream.WriteAsync<string>(context, async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped = true; }
        }, "heartbeat", requestAborted ? CancellationToken.None : cancellation.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await response.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopped);
        Assert.Equal(0, body.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_DisconnectDuringWriteOrFlush_StopsProducerAndReturnsNormally(bool duringFlush)
    {
        using var cancellation = new CancellationTokenSource();
        using var body = new DisconnectingStream(cancellation, duringFlush);
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        context.RequestAborted = cancellation.Token;
        var stopped = false;

        await JsonEventStream.WriteAsync<string>(context, async (send, token) =>
        {
            try
            {
                await send("event");
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally { stopped = true; }
        }, "heartbeat", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopped);
    }

    [Fact]
    public async Task WriteAsync_UnrelatedCancellation_IsNotSilenced()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JsonEventStream.WriteAsync<string>(context,
            (_, _) => Task.FromException(new OperationCanceledException("Provider timed out")), "heartbeat", CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_ProducerFailure_IsNotSilenced()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var error = new InvalidOperationException("Producer failed");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => JsonEventStream.WriteAsync<string>(context,
            (_, _) => Task.FromException(error), "heartbeat", CancellationToken.None));
        Assert.Same(error, actual);
    }

    private sealed class DisconnectingStream(CancellationTokenSource cancellation, bool duringFlush) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!duringFlush)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await cancellation.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
