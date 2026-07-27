using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShopInventory.Middleware;

namespace ShopInventory.Tests;

/// <summary>
/// The unit tests set the endpoint by hand. This one runs a real server so the assumption the
/// registration depends on is actually exercised: that by the time
/// <see cref="SapRequestPriorityMiddleware"/> runs — added after output caching and before the
/// endpoints are mapped — routing has already resolved the endpoint, so the opt-out attribute is
/// visible. If it were not, every endpoint would silently be treated as interactive and the
/// reservation would be worth nothing.
/// </summary>
[Collection("SapConcurrency")]
public sealed class SapRequestPriorityPipelineTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _app = builder.Build();

        _app.UseSapRequestPriority();

        _app.MapGet("/interactive", () => SapRequestPriority.IsInteractive.ToString());
        _app.MapGet("/bulk", () => SapRequestPriority.IsInteractive.ToString())
            .WithMetadata(new SapBackgroundWorkAttribute());

        await _app.StartAsync();

        // Port 0 above means Kestrel picks one; Urls carries what it actually bound.
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Ordinary_endpoint_runs_interactive()
    {
        Assert.Equal("True", await _client.GetStringAsync("/interactive"));
    }

    [Fact]
    public async Task Endpoint_marked_background_is_seen_as_such_by_the_middleware()
    {
        Assert.Equal("False", await _client.GetStringAsync("/bulk"));
    }
}
