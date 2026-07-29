using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ShopInventory.Middleware;

namespace ShopInventory.Tests;

/// <summary>
/// <see cref="SapRequestPriority"/> is ambient process state, so these share the collection that
/// disables parallelisation with the concurrency handler's tests.
/// </summary>
[Collection("SapConcurrency")]
public class SapRequestPriorityMiddlewareTests
{
    [Fact]
    public async Task Request_without_an_endpoint_is_interactive()
    {
        var observed = await ObservePriorityAsync(endpointMetadata: null);

        Assert.True(observed);
    }

    [Fact]
    public async Task Request_on_an_unannotated_endpoint_is_interactive()
    {
        // The default, and what almost every endpoint is: a person waiting on a small read.
        var observed = await ObservePriorityAsync(endpointMetadata: new object());

        Assert.True(observed);
    }

    [Fact]
    public async Task Request_on_an_endpoint_marked_background_is_not_interactive()
    {
        // Reports, backfills and bulk validation arrive over HTTP but behave like jobs. Letting
        // them hold the reservation would queue an approval behind them.
        var observed = await ObservePriorityAsync(endpointMetadata: new SapBackgroundWorkAttribute());

        Assert.False(observed);
    }

    [Fact]
    public async Task Scope_is_released_when_the_endpoint_throws()
    {
        var middleware = new SapRequestPriorityMiddleware(_ => throw new InvalidOperationException("boom"));
        var context = CreateContext(endpointMetadata: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.False(SapRequestPriority.IsInteractive);
    }

    [Fact]
    public async Task Priority_does_not_outlive_the_request()
    {
        await ObservePriorityAsync(endpointMetadata: null);

        Assert.False(SapRequestPriority.IsInteractive);
    }

    [Fact]
    public async Task Suppression_drops_the_ambient_priority_for_fire_and_forget_work()
    {
        // What the 202-returning stock fetch relies on: the request marked itself interactive, and
        // the work it hands off must not inherit that.
        using (SapRequestPriority.BeginInteractive())
        {
            Assert.True(SapRequestPriority.IsInteractive);

            await Task.Run(() =>
            {
                using var background = SapRequestPriority.SuppressInteractive();
                Assert.False(SapRequestPriority.IsInteractive);
            });

            // Suppressing inside the handed-off flow must not disarm the request that started it.
            Assert.True(SapRequestPriority.IsInteractive);
        }
    }

    [Fact]
    public void Suppression_restores_a_nested_depth_rather_than_flattening_it()
    {
        using (SapRequestPriority.BeginInteractive())
        {
            using (SapRequestPriority.BeginInteractive())
            {
                using (SapRequestPriority.SuppressInteractive())
                {
                    Assert.False(SapRequestPriority.IsInteractive);
                }

                Assert.True(SapRequestPriority.IsInteractive);
            }

            // Still inside the outer scope: restoring must have put back depth 2, not depth 1.
            Assert.True(SapRequestPriority.IsInteractive);
        }

        Assert.False(SapRequestPriority.IsInteractive);
    }

    private static async Task<bool> ObservePriorityAsync(object? endpointMetadata)
    {
        var observed = false;
        var middleware = new SapRequestPriorityMiddleware(_ =>
        {
            observed = SapRequestPriority.IsInteractive;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(CreateContext(endpointMetadata));
        return observed;
    }

    private static HttpContext CreateContext(object? endpointMetadata)
    {
        var context = new DefaultHttpContext();

        if (endpointMetadata is not null)
        {
            context.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(endpointMetadata),
                "test"));
        }

        return context;
    }
}
