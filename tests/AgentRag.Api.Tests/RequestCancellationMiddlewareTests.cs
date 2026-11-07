using AgentRag.Api.Services;
using Microsoft.AspNetCore.Http;

public class RequestCancellationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ClientDisconnect_HandlesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var middleware = new RequestCancellationMiddleware(async httpContext =>
        {
            await cancellation.CancelAsync();
            httpContext.RequestAborted.ThrowIfCancellationRequested();
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_UnrelatedCancellation_Propagates()
    {
        var context = new DefaultHttpContext();
        var middleware = new RequestCancellationMiddleware(_ =>
            Task.FromException(new OperationCanceledException("Provider timed out")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_OtherException_PropagatesAfterDisconnect()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var middleware = new RequestCancellationMiddleware(_ =>
            Task.FromException(new InvalidOperationException("Database failed")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
    }
}
