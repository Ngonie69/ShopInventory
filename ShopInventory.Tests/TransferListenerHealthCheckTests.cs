using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Health;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the one thing in either system that notices TransferEventListener has stopped.
/// </summary>
/// <remarks>
/// The failure is quiet by construction. The listener's poll loop catches its own exceptions and
/// leaves the poll window unadvanced, so it keeps answering HTTP from its in-memory store while
/// nothing new is read from SAP; and on this side the webhook it feeds is fire-and-forget, so its
/// silence is indistinguishable from an afternoon with no transfers in it. Nothing else raises an
/// alert, and the damage lands on the till — a transfer made while the listener is down is missing
/// from the day's snapshot, so a cashier is refused stock the warehouse holds.
///
/// The severities are the substance of the check, so they are what is asserted: <c>Degraded</c> for
/// a poll that is merely late, because the listener re-reads the same window next cycle and loses
/// nothing once it recovers; <c>Unhealthy</c> only where recovery no longer helps.
/// </remarks>
public sealed class TransferListenerHealthCheckTests
{
    [Fact]
    public async Task A_current_poll_is_healthy()
    {
        var result = await CheckAsync(FakeListener.Healthy(Poll()));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>
    /// The single most valuable answer this check gives, and the state in which every other signal
    /// looks normal: no transfer is reaching the snapshot and nothing else anywhere says so.
    /// </summary>
    [Fact]
    public async Task An_unreachable_listener_is_unhealthy()
    {
        var result = await CheckAsync(FakeListener.Unreachable("connection refused"));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("unreachable", result.Description);
        Assert.Contains("connection refused", result.Description);
        Assert.Equal(false, result.Data["reachable"]);
    }

    /// <summary>
    /// Late, not broken: the listener re-reads the same window next cycle, so a poll that recovers
    /// loses nothing. Waking someone for this would train them to ignore the check.
    /// </summary>
    [Fact]
    public async Task A_late_poll_is_degraded_rather_than_unhealthy()
    {
        var result = await CheckAsync(FakeListener.Healthy(
            Poll(lastSuccess: DateTime.UtcNow.AddMinutes(-25))));

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task A_poll_stale_past_the_critical_threshold_is_unhealthy()
    {
        var result = await CheckAsync(FakeListener.Healthy(
            Poll(lastSuccess: DateTime.UtcNow.AddMinutes(-90), consecutiveFailures: 12)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("absent from the daily snapshot", result.Description);
    }

    /// <summary>
    /// A listener that has never polled must not read as fresh. With no successful cycle there is no
    /// timestamp to measure from, and the obvious default — treating null as "just now" — would make
    /// a listener that has never worked the healthiest thing on the page.
    /// </summary>
    [Fact]
    public async Task A_listener_that_has_never_polled_is_measured_from_its_start()
    {
        var result = await CheckAsync(FakeListener.Healthy(Poll(
            neverPolled: true,
            processStarted: DateTime.UtcNow.AddHours(-3))));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task A_poll_loop_that_never_started_is_unhealthy()
    {
        var result = await CheckAsync(FakeListener.Healthy(
            Poll(pollingStarted: false, neverPolled: true)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("has not started", result.Description);
    }

    /// <summary>
    /// Detection worked and delivery did not, and nothing retries a failed webhook — so those
    /// documents never reach the snapshot however healthy the listener looks afterwards. A recovered
    /// poll does not undo it, which is why this outranks a merely late one.
    /// </summary>
    [Fact]
    public async Task Failed_webhook_deliveries_are_unhealthy_even_while_polling_is_current()
    {
        var result = await CheckAsync(FakeListener.Healthy(Poll(webhookFailures: 3)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("nothing retries them", result.Description);
    }

    /// <summary>
    /// The two warehouse lists are maintained independently — one in this API's configuration, the
    /// other compiled into the listener — so they drift, and the drift is invisible from either side.
    /// KEFBYS is the live example: this API snapshots the Bulawayo shop and the listener does not
    /// watch it, so that shop is snapshotted at 07:00 and never adjusted again.
    /// </summary>
    [Fact]
    public async Task A_warehouse_the_listener_does_not_watch_is_reported()
    {
        var result = await CheckAsync(
            FakeListener.Healthy(Poll(), watched: ["KEFSHOP"]),
            snapshotted: ["KEFSHOP", "KEFBYS"]);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("KEFBYS", result.Description);
        Assert.Contains("KEFBYS", (string)result.Data["warehousesNotWatchedByListener"]);
    }

    /// <summary>
    /// A failure listing warehouses must not mask the poll status, which is what the caller came for
    /// and which answered successfully to get this far.
    /// </summary>
    [Fact]
    public async Task A_failed_warehouse_listing_does_not_fail_the_check()
    {
        var listener = FakeListener.Healthy(Poll());
        listener.WarehouseListingThrows = true;

        var result = await CheckAsync(listener, snapshotted: ["KEFSHOP", "KEFBYS"]);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task A_disabled_integration_expects_nothing_of_the_listener()
    {
        var result = await CheckAsync(FakeListener.Disabled());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("switched off", result.Description);
    }

    // ── Helpers ─────────────────────────────────────────

    private static async Task<HealthCheckResult> CheckAsync(
        FakeListener listener,
        List<string>? snapshotted = null)
    {
        var check = new TransferListenerHealthCheck(
            listener,
            Options.Create(new TransferEventListenerSettings()),
            Options.Create(new DailyStockSettings
            {
                MonitoredWarehouses = snapshotted ?? ["KEFSHOP"]
            }));

        return await check.CheckHealthAsync(new HealthCheckContext(), default);
    }

    /// <remarks>
    /// <paramref name="neverPolled"/> exists because "no successful cycle" cannot be expressed by
    /// passing null for <paramref name="lastSuccess"/> — null is also what "unspecified" looks like,
    /// and a defaulting helper would quietly hand the test a fresh timestamp and pass regardless.
    /// </remarks>
    private static TransferListenerPollDto Poll(
        bool pollingStarted = true,
        DateTime? lastSuccess = null,
        DateTime? processStarted = null,
        int consecutiveFailures = 0,
        int webhookFailures = 0,
        bool neverPolled = false) => new()
        {
            ProcessStartedUtc = processStarted ?? DateTime.UtcNow.AddHours(-2),
            PollingStarted = pollingStarted,
            LastAttemptUtc = DateTime.UtcNow.AddMinutes(-1),
            LastSuccessUtc = neverPolled || !pollingStarted
                ? null
                : lastSuccess ?? DateTime.UtcNow.AddMinutes(-1),
            PollingSinceUtc = DateTime.UtcNow.AddMinutes(-1),
            ConsecutiveFailures = consecutiveFailures,
            PollIntervalSeconds = 300,
            WebhookFailures = webhookFailures
        };

    private sealed class FakeListener : ITransferEventListenerClient
    {
        private TransferListenerHealthDto? _health;
        private Exception? _failure;
        private bool _enabled = true;
        private IReadOnlyList<string> _watched = ["KEFSHOP"];

        public bool WarehouseListingThrows { get; set; }

        public bool IsEnabled => _enabled;

        public string BaseUrl => "http://listener.test";

        public static FakeListener Healthy(
            TransferListenerPollDto poll,
            IReadOnlyList<string>? watched = null) => new()
            {
                _health = new TransferListenerHealthDto
                {
                    Status = "healthy",
                    Message = "SAP polled successfully.",
                    Poll = poll,
                    MonitoredWarehouseCount = 20,
                    DocumentsSeen = 23,
                    LinesSeen = 244
                },
                _watched = watched ?? ["KEFSHOP"]
            };

        public static FakeListener Unreachable(string message) =>
            new() { _failure = new HttpRequestException(message) };

        public static FakeListener Disabled() => new() { _enabled = false };

        public Task<TransferListenerHealthDto> GetHealthAsync(CancellationToken cancellationToken = default) =>
            _failure is not null
                ? Task.FromException<TransferListenerHealthDto>(_failure)
                : Task.FromResult(_health!);

        public Task<IReadOnlyList<string>> GetMonitoredWarehousesAsync(CancellationToken cancellationToken = default) =>
            WarehouseListingThrows
                ? Task.FromException<IReadOnlyList<string>>(new HttpRequestException("no listing"))
                : Task.FromResult(_watched);

        public Task<TransferListenerWarehouseStockDto?> GetWarehouseNonBatchStockAsync(
            string warehouseCode, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected on the health path.");

        public Task<TransferListenerStatsDto> GetStatsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected on the health path.");

        public Task<TransferListenerCheckResultDto> TriggerCheckAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected on the health path.");
    }
}
