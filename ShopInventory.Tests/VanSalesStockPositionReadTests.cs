using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesStockPosition;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van being able to ask what it is carrying, not only to say so.
/// </summary>
/// <remarks>
/// The handset held the only live count and there was no route to read it back. That is survivable
/// until the handset cannot answer — a reinstall, a replacement device, a handover to the next rep —
/// at which point somebody is standing in front of a customer with no product list, and the obvious
/// fallback is the one figure the codebase already documents as wrong for a van: SAP's, which is a
/// day of trading behind because the sales that moved it are still queued on the handset.
///
/// <para>
/// The position is computed rather than stored, and these tests pin the three terms it is computed
/// from. Nothing about how a van sale is recorded, posted or reported changes — which was the point
/// of computing it here rather than moving where a van sale commits to the stock ledger.
/// </para>
/// </remarks>
public sealed class VanSalesStockPositionReadTests : IDisposable
{
    private const string Van = "VAN001";
    private const string Item = "YOG004";

    private static readonly Guid Rep = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    /// <summary>
    /// The day a van's count is filed under. The post that files it uses the CAT calendar date, not
    /// the 07:00 ledger day, so the read has to agree or it finds nothing every morning before seven.
    /// </summary>
    private static DateTime TradingDate => AuditService.ToCAT(DateTime.UtcNow).Date;

    public VanSalesStockPositionReadTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _context = new SnapshotSqliteContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task The_morning_count_is_what_the_van_is_carrying_before_anything_happens()
    {
        await SeedRepAsync();
        await SeedCountAsync((Item, 40m));

        var result = await Handler().Handle(Query(), default);

        Assert.False(result.IsError);
        Assert.True(result.Value.Counted);

        var line = Assert.Single(result.Value.Lines);
        Assert.Equal(Item, line.Code);
        Assert.Equal(40m, line.OpeningQuantity);
        Assert.Equal(40m, line.Quantity);
    }

    /// <summary>
    /// Sales count whether or not they have posted to SAP. The question is what is on the van, not
    /// what SAP has been told — the opposite of the choice the hourly reconcile makes, and for the
    /// opposite reason.
    /// </summary>
    [Fact]
    public async Task Sales_received_today_come_off_whether_or_not_they_have_posted()
    {
        await SeedRepAsync();
        await SeedCountAsync((Item, 40m));
        await SeedSaleAsync(Item, 6m, DesktopSaleConsolidationStatus.Pending);
        await SeedSaleAsync(Item, 4m, DesktopSaleConsolidationStatus.Consolidated);

        var result = await Handler().Handle(Query(), default);

        var line = Assert.Single(result.Value.Lines);
        Assert.Equal(10m, line.SoldQuantity);
        Assert.Equal(30m, line.Quantity);
    }

    /// <summary>
    /// A mid-day load reaches this system as a transfer adjustment and has no opening row of its own,
    /// so an item can appear on the van having never been counted onto it.
    /// </summary>
    [Fact]
    public async Task Stock_loaded_during_the_day_appears_without_an_opening_row()
    {
        await SeedRepAsync();
        await SeedCountAsync((Item, 40m));
        await SeedTransferAsync("NEW001", 12m);

        var result = await Handler().Handle(Query(), default);

        var line = Assert.Single(result.Value.Lines, candidate => candidate.Code == "NEW001");
        Assert.Equal(0m, line.OpeningQuantity);
        Assert.Equal(12m, line.TransferredQuantity);
        Assert.Equal(12m, line.Quantity);
    }

    /// <summary>
    /// An item sold down to nothing stays on the list. Dropping it would leave a rep looking for it,
    /// finding it missing, and concluding the list is broken rather than that the van is out.
    /// </summary>
    [Fact]
    public async Task An_item_sold_out_is_still_listed_at_zero()
    {
        await SeedRepAsync();
        await SeedCountAsync((Item, 5m));
        await SeedSaleAsync(Item, 5m, DesktopSaleConsolidationStatus.Pending);

        var result = await Handler().Handle(Query(), default);

        var line = Assert.Single(result.Value.Lines);
        Assert.Equal(0m, line.Quantity);
    }

    /// <summary>
    /// A van that has sold more than this system thinks it loaded is a real and known condition — the
    /// end-of-day posting reports it as a shortfall. A negative would read to a rep as stock owed.
    /// </summary>
    [Fact]
    public async Task Selling_past_the_count_floors_at_nothing_left_rather_than_going_negative()
    {
        await SeedRepAsync();
        await SeedCountAsync((Item, 5m));
        await SeedSaleAsync(Item, 9m, DesktopSaleConsolidationStatus.Pending);

        var result = await Handler().Handle(Query(), default);

        var line = Assert.Single(result.Value.Lines);
        Assert.Equal(0m, line.Quantity);
        Assert.Equal(9m, line.SoldQuantity);
    }

    /// <summary>
    /// The safety of the whole route. No opening count means the position is unknown, and an empty
    /// list rendered on a handset says "this van is carrying nothing" — which is both wrong and the
    /// kind of wrong that stops a day's selling.
    /// </summary>
    [Fact]
    public async Task A_van_with_no_opening_count_is_told_so_rather_than_shown_an_empty_van()
    {
        await SeedRepAsync();

        var result = await Handler().Handle(Query(), default);

        Assert.False(result.IsError);
        Assert.False(result.Value.Counted);
        Assert.Empty(result.Value.Lines);
        Assert.Contains("has not filed an opening stock count", result.Value.Message);
    }

    /// <summary>
    /// Another van's count is another van's business. The warehouse comes off the account through the
    /// same resolver the post uses, and is never taken from the caller.
    /// </summary>
    [Fact]
    public async Task A_rep_is_never_shown_a_van_they_do_not_drive()
    {
        await SeedRepAsync();
        await SeedCountAsync((Item, 40m));
        await SeedCountAsync(warehouse: "VAN004", items: (Item, 999m));

        var result = await Handler().Handle(Query(), default);

        Assert.Equal(Van, result.Value.WarehouseCode);
        Assert.Equal(40m, Assert.Single(result.Value.Lines).Quantity);
    }

    [Fact]
    public async Task An_account_with_no_van_is_refused_rather_than_answered_with_nothing()
    {
        await SeedRepAsync(warehouse: null);

        var result = await Handler().Handle(Query(), default);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCompatibility.MissingWarehouse", result.FirstError.Code);
    }

    // ── Helpers ─────────────────────────────────────────

    private GetVanSalesStockPositionHandler Handler() => new(
        _context, NullLogger<GetVanSalesStockPositionHandler>.Instance);

    private static GetVanSalesStockPositionQuery Query() => new(Rep);

    private async Task SeedRepAsync(string? warehouse = Van)
    {
        var user = new User
        {
            Id = Rep,
            Username = "van-rep",
            PasswordHash = "not-a-real-hash",
            Role = ApplicationRoles.Driver,
            IsActive = true
        };

        if (warehouse is not null)
        {
            user.SetWarehouseCodes([warehouse]);
        }

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task SeedCountAsync(
        params (string ItemCode, decimal Quantity)[] items)
        => await SeedCountAsync(Van, items);

    private async Task SeedCountAsync(
        string warehouse,
        params (string ItemCode, decimal Quantity)[] items)
    {
        var snapshot = new DailyStockSnapshotEntity
        {
            SnapshotDate = TradingDate,
            WarehouseCode = warehouse,
            Status = StockSnapshotStatus.Complete
        };

        _context.DailyStockSnapshots.Add(snapshot);
        await _context.SaveChangesAsync();

        foreach (var (itemCode, quantity) in items)
        {
            _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
            {
                SnapshotId = snapshot.Id,
                ItemCode = itemCode,
                ItemDescription = itemCode,
                WarehouseCode = warehouse,
                // Both, the way the handset's post writes them.
                OriginalQuantity = quantity,
                AvailableQuantity = quantity,
                Version = 1
            });
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task SeedTransferAsync(string itemCode, decimal quantity)
    {
        _context.StockTransferAdjustments.Add(new StockTransferAdjustmentEntity
        {
            SnapshotDate = TradingDate,
            ItemCode = itemCode,
            WarehouseCode = Van,
            AdjustmentQuantity = quantity,
            Direction = "IN",
            DetectedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task SeedSaleAsync(
        string itemCode,
        decimal quantity,
        DesktopSaleConsolidationStatus status)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = $"VAN-{Guid.NewGuid():N}",
            CardCode = "CASH",
            WarehouseCode = Van,
            ConsolidationStatus = status,
            // Any instant inside the CAT trading day, which UtcNow always is.
            CreatedAt = DateTime.UtcNow,
            DocDate = DateTime.UtcNow.Date
        };

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();

        _context.DesktopSaleLines.Add(new DesktopSaleLineEntity
        {
            SaleId = sale.Id,
            LineNum = 0,
            ItemCode = itemCode,
            WarehouseCode = Van,
            Quantity = quantity
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
