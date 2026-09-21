using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Caching;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A SAP cache sync raises <c>sap.sync.success</c> or <c>sap.sync.failed</c>; a sync that did not opt
/// in raises nothing.
/// </summary>
/// <remarks>
/// These two event types were declared in <see cref="WebhookEventTypes"/> from the start and had no
/// publisher. <see cref="CacheSyncStateRecorder"/> is where they belong: every SAP-backed cache
/// already reports its outcome through it, once per sync run rather than once per document.
///
/// The opt-in matters and is asserted here. The same recorder serves Cartrack's fleet sync, which is
/// not SAP, so the flag is what separates them — inferring it from the cache key would make any new
/// key silently start claiming SAP syncs.
///
/// Assertions are on the delivery rows rather than on the outgoing POST, for the reason set out in
/// <see cref="WebhookDeliveryOutlivesRequestScopeTests"/>: the row is what survives the request.
/// </remarks>
public sealed class SapSyncWebhookEventTests : IDisposable
{
    private const string SapCacheKey = "BusinessPartners";
    private const string OtherCacheKey = "CartrackFleet";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly CacheSyncStateRecorder _recorder;

    public SapSyncWebhookEventTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using (var seed = NewContext())
        {
            seed.Database.EnsureCreated();
            seed.Webhooks.Add(new Webhook
            {
                Name = "Sync monitor",
                Url = "https://receiver.invalid/hook",
                Events = $"{WebhookEventTypes.SapSyncSuccess},{WebhookEventTypes.SapSyncFailed}",
                IsActive = true,
                RetryCount = 1,
                TimeoutSeconds = 5
            });
            seed.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(
            options => options.UseSqlite(_connection),
            ServiceLifetime.Scoped);
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory());
        services.AddSingleton<ILogger<WebhookService>>(NullLogger<WebhookService>.Instance);
        services.AddScoped<IWebhookService, WebhookService>();

        _provider = services.BuildServiceProvider();

        _recorder = new CacheSyncStateRecorder(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CacheSyncStateRecorder>.Instance);
    }

    [Fact]
    public async Task A_successful_sap_sync_publishes_sap_sync_success()
    {
        await _recorder.RecordSuccessAsync(
            SapCacheKey,
            SapCacheKey,
            itemCount: 412,
            syncedAt: DateTime.UtcNow,
            CancellationToken.None,
            publishSyncEvent: true);

        var delivery = await WaitForDeliveryAsync(WebhookEventTypes.SapSyncSuccess);

        Assert.NotNull(delivery);
        Assert.True(delivery!.IsSuccess);
        Assert.Contains("412", delivery.Payload);

        // The sync state itself must still be recorded — the publish is an addition, not a detour.
        using var context = NewContext();
        var state = await context.CacheSyncStates.SingleAsync(s => s.CacheKey == SapCacheKey);
        Assert.Equal(412, state.ItemCount);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task A_failed_sap_sync_publishes_sap_sync_failed_with_the_recorded_error()
    {
        await _recorder.RecordFailureAsync(
            SapCacheKey,
            SapCacheKey,
            "Service Layer returned 503",
            DateTime.UtcNow,
            CancellationToken.None,
            publishSyncEvent: true);

        var delivery = await WaitForDeliveryAsync(WebhookEventTypes.SapSyncFailed);

        Assert.NotNull(delivery);
        Assert.Contains("Service Layer returned 503", delivery!.Payload);

        using var context = NewContext();
        var state = await context.CacheSyncStates.SingleAsync(s => s.CacheKey == SapCacheKey);
        Assert.Equal("Service Layer returned 503", state.LastError);
    }

    [Fact]
    public async Task A_sync_that_did_not_opt_in_publishes_nothing()
    {
        // Cartrack's fleet sync calls the same recorder without the flag.
        await _recorder.RecordSuccessAsync(
            OtherCacheKey,
            "Cartrack fleet",
            itemCount: 30,
            syncedAt: DateTime.UtcNow,
            CancellationToken.None);

        await _recorder.RecordFailureAsync(
            OtherCacheKey,
            "Cartrack fleet",
            "boom",
            DateTime.UtcNow,
            CancellationToken.None);

        // Nothing to wait for, so wait out the window a real delivery would have landed in. The
        // positive tests above establish that this window is long enough to see one.
        await Task.Delay(1500);

        using var context = NewContext();
        Assert.Empty(await context.WebhookDeliveries.ToListAsync());

        // ...and the state was still recorded, which is this call's actual job.
        var state = await context.CacheSyncStates.SingleAsync(s => s.CacheKey == OtherCacheKey);
        Assert.Equal("boom", state.LastError);
    }

    /// <summary>
    /// Waits for the delivery to be not just logged but finished.
    /// </summary>
    /// <remarks>
    /// The counter save on the hook is the last write the delivery makes, so waiting for it is what
    /// makes the caller's own assertions safe: every context here shares one SQLite connection, and
    /// reading it while the background task still has statements in flight fails the read.
    /// </remarks>
    private Task<WebhookDelivery?> WaitForDeliveryAsync(string eventType)
        => BackgroundWriteProbe.PollAsync<WebhookDelivery>(
            NewContext,
            async context =>
            {
                var delivery = await context.WebhookDeliveries
                    .FirstOrDefaultAsync(d => d.EventType == eventType);

                var settled = await context.Webhooks.AnyAsync(w => w.LastTriggeredAt != null);

                return delivery != null && settled ? delivery : null;
            });

    private ApplicationDbContext NewContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());

        private sealed class StubHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ok")
                });
        }
    }
}
