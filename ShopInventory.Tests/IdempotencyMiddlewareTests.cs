using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Middleware;

namespace ShopInventory.Tests;

public class IdempotencyMiddlewareTests
{
    [Fact]
    public async Task Concurrent_duplicate_is_blocked_while_first_request_is_running()
    {
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var middleware = new IdempotencyMiddleware(
            async context =>
            {
                Interlocked.Increment(ref calls);
                firstEntered.TrySetResult();
                await releaseFirst.Task;
                context.Response.StatusCode = StatusCodes.Status201Created;
            },
            NullLogger<IdempotencyMiddleware>.Instance);

        var key = $"concurrent-{Guid.NewGuid():N}";
        var firstTask = middleware.InvokeAsync(CreateContext(key));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var duplicate = CreateContext(key);
        await middleware.InvokeAsync(duplicate);

        Assert.Equal(StatusCodes.Status409Conflict, duplicate.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref calls));

        releaseFirst.TrySetResult();
        await firstTask;
    }

    [Fact]
    public async Task Server_failure_releases_key_so_request_can_be_retried()
    {
        var calls = 0;
        var middleware = new IdempotencyMiddleware(
            context =>
            {
                var call = Interlocked.Increment(ref calls);
                context.Response.StatusCode = call == 1
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            NullLogger<IdempotencyMiddleware>.Instance);

        var key = $"retry-{Guid.NewGuid():N}";
        var first = CreateContext(key);
        await middleware.InvokeAsync(first);
        var second = CreateContext(key);
        await middleware.InvokeAsync(second);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, second.Response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Exception_releases_key_so_request_can_be_retried()
    {
        var calls = 0;
        var middleware = new IdempotencyMiddleware(
            context =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("simulated failure");

                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            NullLogger<IdempotencyMiddleware>.Instance);

        var key = $"exception-{Guid.NewGuid():N}";
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(CreateContext(key)));

        var retry = CreateContext(key);
        await middleware.InvokeAsync(retry);

        Assert.Equal(StatusCodes.Status204NoContent, retry.Response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("/api/crates/transactions/42/pods")]
    [InlineData("/api/crates/transactions/42/grvs")]
    public async Task Handler_owned_endpoints_are_not_replayed_by_this_middleware(string path)
    {
        // These handlers persist their response through IIdempotencyRequestStore and replay the real
        // document. This middleware only remembers a status code, so if it short-circuited the retry
        // the caller would get a bare message and never learn what was created.
        var calls = 0;
        var middleware = new IdempotencyMiddleware(
            context =>
            {
                Interlocked.Increment(ref calls);
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            NullLogger<IdempotencyMiddleware>.Instance);

        var key = $"handler-owned-{Guid.NewGuid():N}";
        await middleware.InvokeAsync(CreateContext(key, path));
        var retry = CreateContext(key, path);
        await middleware.InvokeAsync(retry);

        Assert.Equal(StatusCodes.Status200OK, retry.Response.StatusCode);
        Assert.False(retry.Response.Headers.ContainsKey("Idempotency-Replayed"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Sibling_crate_routes_keep_the_middleware_guard()
    {
        // ensure-invoice shares the /api/crates/transactions/ prefix but owns no idempotency of its
        // own, so a prefix-only rule would have silently dropped its guard.
        var calls = 0;
        var middleware = new IdempotencyMiddleware(
            context =>
            {
                Interlocked.Increment(ref calls);
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            NullLogger<IdempotencyMiddleware>.Instance);

        var key = $"ensure-invoice-{Guid.NewGuid():N}";
        const string path = "/api/crates/transactions/ensure-invoice";
        await middleware.InvokeAsync(CreateContext(key, path));
        var retry = CreateContext(key, path);
        await middleware.InvokeAsync(retry);

        Assert.Equal("true", retry.Response.Headers["Idempotency-Replayed"]);
        Assert.Equal(1, calls);
    }

    private static DefaultHttpContext CreateContext(string key, string path = "/api/salesorder")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.Headers["Idempotency-Key"] = key;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
