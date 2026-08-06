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

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status409Conflict)]
    public async Task Client_error_releases_key_so_request_can_be_retried(int failureStatusCode)
    {
        // A refused request creates no document, so there is nothing for the key to protect.
        // Remembering it locked reps out of their own draft: a sales order refused on credit came
        // back as 400, and every later attempt with that draft's stable key was answered
        // "duplicate request" — hiding the credit refusal that would have told them what to fix.
        var calls = 0;
        var middleware = new IdempotencyMiddleware(
            context =>
            {
                var call = Interlocked.Increment(ref calls);
                context.Response.StatusCode = call == 1
                    ? failureStatusCode
                    : StatusCodes.Status201Created;
                return Task.CompletedTask;
            },
            NullLogger<IdempotencyMiddleware>.Instance);

        var key = $"client-error-{Guid.NewGuid():N}";
        var first = CreateContext(key, "/api/creditnote");
        await middleware.InvokeAsync(first);
        var retry = CreateContext(key, "/api/creditnote");
        await middleware.InvokeAsync(retry);

        Assert.Equal(failureStatusCode, first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status201Created, retry.Response.StatusCode);
        Assert.False(retry.Response.Headers.ContainsKey("Idempotency-Replayed"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Success_is_still_replayed_rather_than_run_twice()
    {
        var calls = 0;
        var middleware = new IdempotencyMiddleware(
            context =>
            {
                Interlocked.Increment(ref calls);
                context.Response.StatusCode = StatusCodes.Status201Created;
                return Task.CompletedTask;
            },
            NullLogger<IdempotencyMiddleware>.Instance);

        var key = $"success-{Guid.NewGuid():N}";
        await middleware.InvokeAsync(CreateContext(key, "/api/creditnote"));
        var retry = CreateContext(key, "/api/creditnote");
        await middleware.InvokeAsync(retry);

        Assert.Equal(StatusCodes.Status201Created, retry.Response.StatusCode);
        Assert.Equal("true", retry.Response.Headers["Idempotency-Replayed"]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Keyless_sales_order_is_still_refused_without_a_key()
    {
        // Owning the replay is not the same as being allowed to arrive without a key: the 428 guard
        // has to survive the route moving into the handler-owned set.
        var calls = 0;
        var middleware = new IdempotencyMiddleware(
            context =>
            {
                Interlocked.Increment(ref calls);
                context.Response.StatusCode = StatusCodes.Status201Created;
                return Task.CompletedTask;
            },
            NullLogger<IdempotencyMiddleware>.Instance);

        var keyless = CreateContext(key: null, "/api/salesorder");
        await middleware.InvokeAsync(keyless);

        Assert.Equal(StatusCodes.Status428PreconditionRequired, keyless.Response.StatusCode);
        Assert.Equal(0, calls);
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
    // The POD upload deduplicates on external reference and on file content, returning the
    // attachment either way; the crate variant delegates to the crates handler above.
    [InlineData("/api/invoice/2148037/pod")]
    [InlineData("/api/invoice/2148037/crate-pod")]
    // SalesOrderService.CreateAsync looks the ClientRequestId up before inserting, and again when
    // the unique index rejects a racing insert, returning the existing order both times.
    [InlineData("/api/salesorder")]
    // CreateInvoiceHandler completes its store entry with the InvoiceCreatedResponseDto, so the
    // retry gets the DocEntry and DocNum rather than the remembered status code and a bare message.
    [InlineData("/api/invoice")]
    public async Task Handler_owned_endpoints_are_not_replayed_by_this_middleware(string path)
    {
        // These handlers persist their own key and replay the real document. This middleware only
        // remembers a status code, so if it short-circuited the retry the caller would get a bare
        // message and never learn what was created.
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

    [Theory]
    [InlineData("/api/invoice/2148037/fiscalize")]
    public async Task Sibling_invoice_routes_keep_the_middleware_guard(string path)
    {
        // "POST /api/invoice/" + "/pod" must not widen into a prefix rule over the whole invoice
        // controller: everything else under it still relies on this middleware. The create route
        // itself is no longer the witness for that — it owns its replay, and moved to the theory
        // above — so fiscalize carries it: a sub-route under the same prefix, owning nothing.
        var calls = 0;
        var middleware = new IdempotencyMiddleware(
            context =>
            {
                Interlocked.Increment(ref calls);
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            NullLogger<IdempotencyMiddleware>.Instance);

        var key = $"invoice-sibling-{Guid.NewGuid():N}";
        await middleware.InvokeAsync(CreateContext(key, path));
        var retry = CreateContext(key, path);
        await middleware.InvokeAsync(retry);

        Assert.Equal("true", retry.Response.Headers["Idempotency-Replayed"]);
        Assert.Equal(1, calls);
    }

    // Defaults to an endpoint this middleware still guards, so the tests above describe its own
    // behaviour rather than some endpoint's ownership. /api/salesorder and /api/invoice are not
    // ones: both handlers persist their own key and replay the real document.
    private static DefaultHttpContext CreateContext(string? key, string path = "/api/creditnote")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        if (key is not null)
            context.Request.Headers["Idempotency-Key"] = key;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
