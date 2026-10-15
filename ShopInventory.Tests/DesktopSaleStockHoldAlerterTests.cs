using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A till sale held off the shelf because it is not in SAP must never have that hold end without an
/// admin having been told.
/// </summary>
/// <remarks>
/// Built on sale 12 at KEFSHOP, KEF-FAC-20260914-8DFB97306920: rung up 14 Sep 2026 14:04 CAT, its six
/// posting attempts spent in a HANA outage, held for thirty days, and released at the 07:00 fetch on
/// 15 Oct with nobody told at any point.
/// </remarks>
public sealed class DesktopSaleStockHoldAlerterTests : IDisposable
{
    private const string Shop = "KEFSHOP";
    private const string FetchTime = "07:00";
    private const int LookbackDays = 30;

    // 14 Sep 14:04 CAT.
    private static readonly DateTime Sale12CreatedAt = new(2026, 9, 14, 12, 4, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly NoOpNotificationService _notifications = new();
    private readonly SapCircuitBreakerState _circuit = new(Options.Create(new SAPSettings()));

    public DesktopSaleStockHoldAlerterTests()
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

    // ── When the hold ends ───────────────────────────────

    [Fact]
    public void Sale_12s_hold_ended_at_the_07_00_fetch_on_15_October()
    {
        var releaseAt = UnpostedTillSales.HoldEndsAtUtc(Sale12CreatedAt, FetchTime, LookbackDays);

        Assert.Equal(new DateTime(2026, 10, 15, 5, 0, 0, DateTimeKind.Utc), releaseAt);
    }

    [Theory]
    [InlineData("2026-09-14T12:04:00Z")]
    [InlineData("2026-09-14T05:00:00Z")] // exactly at a ledger-day start
    [InlineData("2026-09-14T04:59:59Z")]
    [InlineData("2026-09-14T23:30:00Z")] // after midnight CAT, before the fetch
    public async Task The_release_time_agrees_with_the_netting_itself(string created)
    {
        var createdAt = DateTime.Parse(created, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        await SeedSaleAsync(createdAt, attempts: 6);

        var releaseAt = UnpostedTillSales.HoldEndsAtUtc(createdAt, FetchTime, LookbackDays);
        var lastHeldDay = StockLedgerDay.Resolve(releaseAt.AddSeconds(-1), StockLedgerDay.ParseFetchTime(FetchTime));
        var releasedDay = StockLedgerDay.Resolve(releaseAt, StockLedgerDay.ParseFetchTime(FetchTime));

        Assert.True(await HeldOnAsync(lastHeldDay));
        Assert.False(await HeldOnAsync(releasedDay));
    }

    // ── Stuck ────────────────────────────────────────────

    [Fact]
    public async Task A_sale_whose_posting_budget_is_spent_is_reported_to_the_admins_at_once()
    {
        var sale = await SeedSaleAsync(Sale12CreatedAt, attempts: 6,
            error: "SAP could not be asked whether it already holds this invoice");

        var sent = await Alerter().AlertAsync(Sale12CreatedAt.AddMinutes(10));

        Assert.Equal(1, sent);
        var notification = Assert.Single(_notifications.Sent);
        Assert.Equal("Admin", notification.TargetRole);
        Assert.Equal("Till sale stuck before SAP: " + sale.ExternalReferenceId, notification.Title);
        Assert.Contains("posting failed 6 times and has stopped retrying", notification.Message);
        Assert.Contains("held until 15 Oct 07:00", notification.Message);
        Assert.Equal(sale.Id.ToString(), notification.EntityId);
        Assert.Equal("/desktop-sales", notification.ActionUrl);
        Assert.Equal(DesktopSaleStockHoldAlert.Stuck, await StageAsync(sale.Id));
    }

    [Fact]
    public async Task A_stage_is_reported_once()
    {
        await SeedSaleAsync(Sale12CreatedAt, attempts: 6);

        await Alerter().AlertAsync(Sale12CreatedAt.AddMinutes(10));
        var second = await Alerter().AlertAsync(Sale12CreatedAt.AddHours(5));

        Assert.Equal(0, second);
        Assert.Single(_notifications.Sent);
    }

    [Fact]
    public async Task A_young_sale_still_being_retried_is_left_alone()
    {
        await SeedSaleAsync(Sale12CreatedAt, attempts: 2);

        Assert.Equal(0, await Alerter().AlertAsync(Sale12CreatedAt.AddHours(3)));
        Assert.Empty(_notifications.Sent);
    }

    [Fact]
    public async Task A_sale_not_in_SAP_after_the_threshold_is_reported_whatever_its_attempts()
    {
        var sale = await SeedSaleAsync(Sale12CreatedAt, attempts: 0,
            fiscal: DesktopSaleFiscalizationStatus.Failed, fiscalError: "RCPT019");

        await Alerter().AlertAsync(Sale12CreatedAt.AddHours(5));

        var notification = Assert.Single(_notifications.Sent);
        Assert.Contains("not fiscalised, so it cannot post: RCPT019", notification.Message);
        Assert.Equal(DesktopSaleStockHoldAlert.Stuck, await StageAsync(sale.Id));
    }

    [Fact]
    public async Task A_sale_in_SAP_is_never_reported()
    {
        await SeedSaleAsync(Sale12CreatedAt, attempts: 6, status: DesktopSaleConsolidationStatus.Consolidated);

        Assert.Equal(0, await Alerter().AlertAsync(Sale12CreatedAt.AddDays(31)));
    }

    [Fact]
    public async Task While_SAP_is_down_age_alone_reports_nothing_but_a_spent_budget_still_does()
    {
        var waiting = await SeedSaleAsync(Sale12CreatedAt, attempts: 0);
        var spent = await SeedSaleAsync(Sale12CreatedAt, attempts: 6);

        for (var i = 0; i < 10; i++)
        {
            _circuit.RecordFailure("Failed to connect to kfldb port 40020");
        }

        await Alerter().AlertAsync(Sale12CreatedAt.AddHours(5));

        var notification = Assert.Single(_notifications.Sent);
        Assert.Contains(spent.ExternalReferenceId, notification.Message);
        Assert.DoesNotContain(waiting.ExternalReferenceId, notification.Message);
        Assert.Equal(DesktopSaleStockHoldAlert.None, await StageAsync(waiting.Id));
    }

    [Fact]
    public async Task Sales_stuck_together_are_reported_in_one_notification()
    {
        var sales = new List<DesktopSaleEntity>();

        for (var i = 0; i < 12; i++)
        {
            sales.Add(await SeedSaleAsync(Sale12CreatedAt.AddMinutes(i), attempts: 6));
        }

        await Alerter().AlertAsync(Sale12CreatedAt.AddHours(1));

        var notification = Assert.Single(_notifications.Sent);
        Assert.Equal("12 till sales stuck before SAP", notification.Title);
        Assert.Null(notification.EntityId);
        // Whole sales are dropped to fit the column; the rest are counted and the instruction survives.
        Assert.True(notification.Message.Length <= 1000);
        Assert.EndsWith("whether or not SAP has the invoice.", notification.Message);
        var listed = sales.Count(s => notification.Message.Contains(s.ExternalReferenceId));
        Assert.InRange(listed, 1, 8);
        Assert.Contains($"And {12 - listed} more", notification.Message);
        Assert.Equal(12, notification.Data!["saleIds"].Split(',').Length);

        foreach (var sale in sales)
        {
            Assert.Equal(DesktopSaleStockHoldAlert.Stuck, await StageAsync(sale.Id));
        }
    }

    // ── Release due and released ─────────────────────────

    [Fact]
    public async Task The_admins_are_reminded_before_the_hold_ends()
    {
        var sale = await SeedSaleAsync(Sale12CreatedAt, attempts: 6);
        await Alerter().AlertAsync(Sale12CreatedAt.AddMinutes(10));

        // 12 Oct 08:00 CAT: inside the three days before 15 Oct 07:00.
        await Alerter().AlertAsync(new DateTime(2026, 10, 12, 6, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, _notifications.Sent.Count);
        Assert.Equal("Stock hold ending: " + sale.ExternalReferenceId + " still not in SAP", _notifications.Sent[1].Title);
        Assert.Equal(DesktopSaleStockHoldAlert.ReleaseDue, await StageAsync(sale.Id));
    }

    [Fact]
    public async Task Sale_12_is_reported_released_the_morning_its_units_went_back_on_sale()
    {
        var sale = await SeedSaleAsync(Sale12CreatedAt, attempts: 6);

        // Never alerted before (the alert did not exist): only the furthest stage goes out.
        await Alerter().AlertAsync(new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc));

        var notification = Assert.Single(_notifications.Sent);
        Assert.Equal("Stock released for unposted sale " + sale.ExternalReferenceId, notification.Title);
        Assert.Equal("Error", notification.Type);
        Assert.Contains("released 15 Oct 07:00", notification.Message);
        Assert.Contains("rather than refreshing quantities from SAP", notification.Message);
        Assert.Equal(DesktopSaleStockHoldAlert.Released, await StageAsync(sale.Id));

        Assert.Equal(0, await Alerter().AlertAsync(new DateTime(2026, 10, 16, 6, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task A_release_long_past_is_not_announced()
    {
        await SeedSaleAsync(Sale12CreatedAt, attempts: 6);

        Assert.Equal(0, await Alerter().AlertAsync(new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task Legacy_desktop_sales_are_not_held_and_not_reported()
    {
        await SeedSaleAsync(Sale12CreatedAt, attempts: 6, source: SaleSourceSystems.LegacyDesktop);

        Assert.Equal(0, await Alerter().AlertAsync(Sale12CreatedAt.AddDays(2)));
    }

    // ── Helpers ──────────────────────────────────────────

    private DesktopSaleStockHoldAlerter Alerter() => new(
        _context,
        _notifications,
        _circuit,
        Options.Create(new DesktopSalePostingSettings()),
        Options.Create(new DailyStockSettings { StockFetchTimeCAT = FetchTime, UnpostedSaleLookbackDays = LookbackDays }),
        NullLogger<DesktopSaleStockHoldAlerter>.Instance);

    private async Task<bool> HeldOnAsync(DateTime ledgerDay)
    {
        var outstanding = await UnpostedTillSales.OutstandingAsync(
            _context, Shop, ledgerDay, FetchTime, LookbackDays, NullLogger.Instance, CancellationToken.None);

        return outstanding.GetValueOrDefault("RMA001") > 0;
    }

    private async Task<DesktopSaleStockHoldAlert> StageAsync(int saleId) =>
        await _context.DesktopSales.AsNoTracking()
            .Where(s => s.Id == saleId)
            .Select(s => s.StockHoldAlert)
            .SingleAsync();

    private async Task<DesktopSaleEntity> SeedSaleAsync(
        DateTime createdAt,
        int attempts,
        DesktopSaleConsolidationStatus status = DesktopSaleConsolidationStatus.Pending,
        DesktopSaleFiscalizationStatus fiscal = DesktopSaleFiscalizationStatus.Success,
        string? error = null,
        string? fiscalError = null,
        string source = SaleSourceSystems.ShopTill)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = $"KEF-FAC-{Guid.NewGuid():N}"[..29],
            SourceSystem = source,
            CardCode = "CIS006",
            Currency = "USD",
            TotalAmount = 18.87m,
            WarehouseCode = Shop,
            ConsolidationStatus = status,
            FiscalizationStatus = fiscal,
            FiscalError = fiscalError,
            PostingAttempts = attempts,
            LastPostingError = error,
            CreatedAt = createdAt,
            DocDate = createdAt.Date
        };

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();

        _context.DesktopSaleLines.Add(new DesktopSaleLineEntity
        {
            SaleId = sale.Id,
            LineNum = 1,
            ItemCode = "RMA001",
            WarehouseCode = Shop,
            Quantity = 40
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return sale;
    }
}
