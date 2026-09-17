using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransferListenerStatus;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// /transfer-listener judges whether a transfer reached local stock from this API's ledger, not from
/// the listener's word.
/// </summary>
/// <remarks>
/// On 2026-09-17 the page said the listener was "reading SAP normally" with 33 webhooks delivered and
/// none failed, while not one of the 237 lines it had found since the previous morning had reached the
/// ledger: it was posting them to a port nothing listened on and queueing each 404. The "delivered"
/// figure was its batch-sync call. These tests hold the page to the two things that would have shown
/// it — the listener's retry queue, and the adjustments table the transfer handler writes.
/// </remarks>
public sealed class TransferListenerStatusLedgerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly DateTime _today = StockLedgerDay.Today("07:00");

    public TransferListenerStatusLedgerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_document_behind_a_stuck_queue_is_waiting_and_the_delivery_error_is_passed_through()
    {
        var oldestPending = DateTime.UtcNow.AddHours(-4);
        var listener = new FakeListener(
            Poll(pending: 106, oldestPending: oldestPending, abandoned: 131,
                error: "HTTP 404", url: "http://10.10.10.9/api/desktopintegration/webhook/transfer-event"),
            Document(88327, DateTime.UtcNow.AddMinutes(-8), batchSyncSucceeded: true));

        var result = (await HandleAsync(listener)).Value;

        var delivery = Assert.IsType<TransferListenerDeliverySummary>(result.Delivery);
        Assert.Equal(106, delivery.PendingLines);
        Assert.Equal(131, delivery.AbandonedLines);
        Assert.Equal("HTTP 404", delivery.LastError);
        Assert.Equal("http://10.10.10.9/api/desktopintegration/webhook/transfer-event", delivery.WebhookUrl);
        Assert.InRange(delivery.MinutesOldestPending!.Value, 239, 241);

        // The listener's own flag says the batch sync succeeded; that must not read as applied.
        var document = Assert.Single(result.RecentDocuments);
        Assert.Equal(GetTransferListenerStatusHandler.Waiting, document.LocalStock);
        Assert.Null(document.AppliedAtUtc);

        Assert.Equal(0, result.Ledger.MovementsToday);
        Assert.True(result.Ledger.Available);
    }

    [Fact]
    public async Task A_document_the_ledger_holds_is_applied_and_counted_for_today()
    {
        var appliedAt = DateTime.UtcNow.AddMinutes(-7);
        AddAdjustment(docEntry: 127294, docNum: 88327, "KEFGRC", "OUT", "YOG155", appliedAt);
        AddAdjustment(docEntry: 127294, docNum: 88327, "VAN006", "IN", "YOG155", appliedAt);
        AddAdjustment(docEntry: 127294, docNum: 88327, "VAN006", "IN", "YOG029", appliedAt);
        AddAdjustment(docEntry: 127001, docNum: 88100, "KEFSHOP", "IN", "JUM001", appliedAt.AddHours(-1));
        await _context.SaveChangesAsync();

        var listener = new FakeListener(Poll(), Document(88327, DateTime.UtcNow.AddMinutes(-8)));

        var result = (await HandleAsync(listener)).Value;

        var document = Assert.Single(result.RecentDocuments);
        Assert.Equal(GetTransferListenerStatusHandler.Applied, document.LocalStock);
        Assert.NotNull(document.AppliedAtUtc);

        Assert.Equal(4, result.Ledger.MovementsToday);
        Assert.Equal(2, result.Ledger.DocumentsToday);
        Assert.Equal(1, result.Ledger.DocumentsTodayByWarehouse["VAN006"]);
        Assert.Equal(1, result.Ledger.DocumentsTodayByWarehouse["KEFGRC"]);
        Assert.Equal(88100, result.Ledger.LastAppliedDocNum);
    }

    /// <summary>
    /// Accepted and moved nothing — a warehouse with no snapshot today does exactly this. With no
    /// queue to blame it is its own finding, not "waiting".
    /// </summary>
    [Fact]
    public async Task A_document_with_nothing_waiting_and_nothing_in_the_ledger_is_not_applied()
    {
        var listener = new FakeListener(Poll(), Document(88301, DateTime.UtcNow.AddMinutes(-30)));

        var result = (await HandleAsync(listener)).Value;

        Assert.Equal(GetTransferListenerStatusHandler.NotApplied, Assert.Single(result.RecentDocuments).LocalStock);
    }

    [Fact]
    public async Task A_document_older_than_the_oldest_waiting_line_is_not_blamed_on_the_queue()
    {
        var listener = new FakeListener(
            Poll(pending: 3, oldestPending: DateTime.UtcNow.AddMinutes(-10)),
            Document(88200, DateTime.UtcNow.AddHours(-2)));

        var result = (await HandleAsync(listener)).Value;

        Assert.Equal(GetTransferListenerStatusHandler.NotApplied, Assert.Single(result.RecentDocuments).LocalStock);
    }

    [Fact]
    public async Task The_ledger_is_read_even_when_the_listener_cannot_be()
    {
        AddAdjustment(docEntry: 1, docNum: 10, "KEFSHOP", "IN", "A", DateTime.UtcNow.AddMinutes(-5));
        await _context.SaveChangesAsync();

        var result = (await HandleAsync(FakeListener.Unreachable())).Value;

        Assert.False(result.Reachable);
        Assert.Null(result.Delivery);
        Assert.Equal(1, result.Ledger.MovementsToday);
    }

    /// <summary>
    /// A listener older than its retry queue sends none of the delivery members. That has to come
    /// through as zero, not as a failed read of the whole status.
    /// </summary>
    [Fact]
    public async Task A_listener_without_delivery_fields_reads_as_nothing_waiting()
    {
        var result = (await HandleAsync(new FakeListener(Poll()))).Value;

        var delivery = Assert.IsType<TransferListenerDeliverySummary>(result.Delivery);
        Assert.Equal(0, delivery.PendingLines);
        Assert.Null(delivery.MinutesOldestPending);
        Assert.Null(delivery.WebhookUrl);
    }

    /// <summary>
    /// Both ledger reads compile against PostgreSQL. This suite runs on SQLite, which cannot see
    /// PostgreSQL's two timestamp types; and the handler swallows a failed read into "no ledger", which
    /// on this page reads as every transfer missing. Pointed at a port with nothing behind it, a
    /// translated query fails in the driver and an untranslatable one fails before it — so every logged
    /// failure has to be Npgsql's.
    /// </summary>
    [Fact]
    public async Task The_ledger_reads_translate_for_PostgreSQL()
    {
        await using var postgres = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=translation_only;Username=none;Password=none;Timeout=1")
                .Options);

        var logger = new CapturingLogger();
        var handler = new GetTransferListenerStatusHandler(
            new FakeListener(Poll(), Document(88327, DateTime.UtcNow.AddMinutes(-8))),
            postgres,
            Options.Create(new DailyStockSettings { StockFetchTimeCAT = "07:00" }),
            logger);

        var result = (await handler.Handle(new GetTransferListenerStatusQuery(), CancellationToken.None)).Value;

        Assert.False(result.Ledger.Available);
        Assert.Equal(2, logger.Exceptions.Count);
        Assert.All(logger.Exceptions, exception => Assert.True(
            ReachedTheDriver(exception),
            "A ledger read failed before Npgsql was asked to run it:" + Environment.NewLine + exception));
    }

    private static bool ReachedTheDriver(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.NpgsqlException)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<GetTransferListenerStatusHandler>
    {
        public List<Exception> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
    }

    private Task<ErrorOr.ErrorOr<TransferListenerStatusResult>> HandleAsync(FakeListener listener) =>
        new GetTransferListenerStatusHandler(
                listener,
                _context,
                Options.Create(new DailyStockSettings { StockFetchTimeCAT = "07:00" }),
                NullLogger<GetTransferListenerStatusHandler>.Instance)
            .Handle(new GetTransferListenerStatusQuery(), CancellationToken.None);

    private void AddAdjustment(int docEntry, int docNum, string warehouse, string direction, string item, DateTime at) =>
        _context.StockTransferAdjustments.Add(new StockTransferAdjustmentEntity
        {
            SnapshotDate = _today,
            ItemCode = item,
            WarehouseCode = warehouse,
            AdjustmentQuantity = direction == "IN" ? 1 : -1,
            Direction = direction,
            TransferDocEntry = docEntry,
            TransferDocNum = docNum,
            SourceWarehouse = "KEFGRC",
            DestinationWarehouse = warehouse,
            DetectedAt = at
        });

    private static TransferListenerPollDto Poll(
        int pending = 0,
        DateTime? oldestPending = null,
        int abandoned = 0,
        string? error = null,
        string? url = null) => new()
        {
            ProcessStartedUtc = DateTime.UtcNow.AddDays(-1),
            PollingStarted = true,
            LastAttemptUtc = DateTime.UtcNow.AddMinutes(-1),
            LastSuccessUtc = DateTime.UtcNow.AddMinutes(-1),
            PollIntervalSeconds = 120,
            PendingNotifications = pending,
            OldestPendingNotificationUtc = oldestPending,
            AbandonedNotifications = abandoned,
            LastDeliveryError = error,
            LastDeliveryErrorUtc = error is null ? null : DateTime.UtcNow.AddMinutes(-1),
            WebhookUrl = url
        };

    private static TransferListenerDocumentDto Document(int docNum, DateTime detectedAt, bool batchSyncSucceeded = true) => new()
    {
        SapDocNum = docNum,
        DetectedAt = detectedAt,
        Direction = "IN",
        MonitoredWarehouse = "VAN006",
        SourceWarehouse = "KEFGRC",
        DestinationWarehouse = "VAN006",
        WebhookSuccess = batchSyncSucceeded,
        LineCount = 1
    };

    private sealed class FakeListener(
        TransferListenerPollDto? poll,
        params TransferListenerDocumentDto[] documents) : ITransferEventListenerClient
    {
        private bool _unreachable;

        public static FakeListener Unreachable() => new(null) { _unreachable = true };

        public bool IsEnabled => true;

        public string BaseUrl => "http://listener.test";

        public Task<TransferListenerHealthDto> GetHealthAsync(CancellationToken cancellationToken = default) =>
            _unreachable
                ? Task.FromException<TransferListenerHealthDto>(new HttpRequestException("connection refused"))
                : Task.FromResult(new TransferListenerHealthDto { Status = "healthy", Poll = poll });

        public Task<TransferListenerStatsDto> GetStatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransferListenerStatsDto { RecentDocuments = [.. documents] });

        public Task<IReadOnlyList<string>> GetMonitoredWarehousesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["KEFGRC", "VAN006"]);

        public Task<TransferListenerWarehouseStockDto?> GetWarehouseNonBatchStockAsync(
            string warehouseCode, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected on the status path.");

        public Task<TransferListenerCheckResultDto> TriggerCheckAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected on the status path.");
    }
}
