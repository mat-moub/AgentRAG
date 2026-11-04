using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;

namespace AgentRag.Api.Services;

internal static class JsonEventStream
{
    public static async Task WriteAsync<T>(HttpContext context,
        Func<Func<T, ValueTask>, CancellationToken, Task> produce, T heartbeat, CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-store, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        using var clientCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, context.RequestAborted);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(clientCancellation.Token);
        var events = Channel.CreateBounded<T>(new BoundedChannelOptions(32) { SingleReader = true });
        var keepAlive = KeepAliveAsync();
        var producer = ProduceAsync();
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(clientCancellation.Token))
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(item, JsonSerializerOptions.Web) + "\n", clientCancellation.Token);
                await context.Response.Body.FlushAsync(clientCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (clientCancellation.IsCancellationRequested)
        {
            // A disconnected client is a normal end to the stream, not a server failure.
        }
        finally
        {
            await cancellation.CancelAsync();
            await producer;
        }

        async Task KeepAliveAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), timeProvider ?? TimeProvider.System);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellation.Token)) events.Writer.TryWrite(heartbeat);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        async Task ProduceAsync()
        {
            Exception? error = null;
            try { await produce(item => events.Writer.WriteAsync(item, cancellation.Token), cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { error = ex; }
            finally
            {
                await cancellation.CancelAsync();
                await keepAlive;
                events.Writer.TryComplete(error);
            }
        }
    }
}
