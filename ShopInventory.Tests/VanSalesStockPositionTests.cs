using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility.Commands.ReportVanSalesStockPosition;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van counting what it is carrying — taken, and never filed as the van's opening stock.
///
/// The count used to become the van's snapshot for the day when it beat the 07:00 read from SAP, and on
/// 2026-09-29 that put VAN005's stock into VAN004's day: the handset still held VAN005's ledger, and the
/// server filed it under the account's new warehouse. A van's day now opens on SAP's figure alone, which
/// is current by the morning since van sales post during the day.
///
/// The answer is still <c>accepted</c>, because that is what makes every handset in the field mark the
/// day filed and stop resending.
/// </summary>
public sealed class VanSalesStockPositionTests : IDisposable
{
    private static readonly Guid VanUser = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesStockPositionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
        GiveTheConcurrencyTokenADefault();

        _context.Users.Add(new User
        {
            Id = VanUser,
            Username = "van006",
            Email = "van006@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = "VAN006",
            AssignedCostCentreCode = "CC006"
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Lets SQLite insert a snapshot item, which it otherwise refuses.
    /// </summary>
    /// <remarks>
    /// <c>DailyStockSnapshotItemEntity.Version</c> is a <c>[Timestamp] uint</c> mapped to PostgreSQL's
    /// <c>xmin</c> — a system column the server fills itself, so EF never writes it. <c>EnsureCreated</c>
    /// on SQLite has no such concept and makes it an ordinary <c>NOT NULL</c> column with no default, so
    /// every insert fails on a constraint that does not exist in production.
    ///
    /// <para>The two existing suites that touch these rows side-step it by inserting through raw SQL and
    /// naming <c>Version</c> themselves. That is not available here: the whole point is to exercise the
    /// handler's own write. So the fixture gives the column the default the real database effectively
    /// has, by rebuilding the empty table from its own DDL — which keeps the rest of the schema exactly
    /// as EF declared it rather than restating it here to drift later.</para>
    /// </remarks>
    private void GiveTheConcurrencyTokenADefault()
    {
        const string table = "DailyStockSnapshotItems";

        using var read = _connection.CreateCommand();
        read.CommandText = $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{table}'";
        var ddl = read.ExecuteScalar() as string
            ?? throw new InvalidOperationException($"{table} was not created.");

        var patched = ddl.Replace(
            "\"Version\" INTEGER NOT NULL",
            "\"Version\" INTEGER NOT NULL DEFAULT 1");

        Assert.NotEqual(ddl, patched);

        using var rebuild = _connection.CreateCommand();
        rebuild.CommandText = $"DROP TABLE \"{table}\"; {patched};";
        rebuild.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private ReportVanSalesStockPositionHandler BuildHandler() =>
        new(_context, NullLogger<ReportVanSalesStockPositionHandler>.Instance);

    private async Task<VanSalesStockPositionResponse> ReportAsync(VanSalesStockPositionRequest request)
    {
        var result = await BuildHandler().Handle(
            new ReportVanSalesStockPositionCommand(request, VanUser),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    /// <summary>
    /// The day the handset counted, derived rather than written down.
    /// </summary>
    /// <remarks>
    /// This was a date literal, and it passed every day until it didn't.
    /// <see cref="ShopInventory.Common.Mobile.CaptureClock"/> refuses a claim older than thirty days
    /// and quietly falls back to the server's own clock, so a hard-coded capture date is a test with
    /// an expiry on it: this one turned over mid-session, exactly thirty days on, during a merge it
    /// had nothing to do with. Three days back is inside every bound CaptureClock applies and stays
    /// there.
    /// </remarks>
    private static DateTime CapturedDay => AuditService.ToCAT(DateTime.UtcNow).Date.AddDays(-3);

    /// <summary>A CAT wall-clock time on <see cref="CapturedDay"/>, in the shape a handset sends.</summary>
    private static string CapturedAtCat(string timeOfDay) => $"{CapturedDay:yyyy-MM-dd}T{timeOfDay}";

    private static VanSalesStockPositionRequest BuildPosition(decimal quantity = 24m) => new()
    {
        CapturedAt = CapturedAtCat("05:42:11"),
        ClientReference = $"VAN006-STK-{CapturedDay:yyyyMMdd}-AAA111",
        Lines =
        [
            new VanSalesStockPositionLineRequest
            {
                Code = "CHE011",
                Description = "Cheese 1kg",
                Batch = "B2609",
                Quantity = quantity,
                UoMCode = "KG",
                ExpiryDate = "2026-09-30"
            }
        ]
    };

    /// <summary>
    /// The count is answered as accepted, and no snapshot is written for it.
    /// </summary>
    [Fact]
    public async Task A_reported_count_is_accepted_and_does_not_become_the_vans_snapshot()
    {
        var response = await ReportAsync(BuildPosition());

        Assert.True(response.Accepted);
        Assert.False(response.Duplicate);
        Assert.Equal("VAN006", response.WarehouseCode);
        Assert.Equal(1, response.LineCount);

        Assert.Empty(_context.DailyStockSnapshots);
        Assert.Empty(_context.DailyStockSnapshotItems);
    }

    /// <summary>
    /// The 2026-09-29 case. The account has been moved to another van while its handset still holds the
    /// old van's ledger; the count used to be filed as the new van's opening position. Now it changes
    /// nothing, and the new van opens on SAP's own figure.
    /// </summary>
    [Fact]
    public async Task A_count_from_a_handset_still_holding_another_vans_ledger_changes_nothing()
    {
        var user = await _context.Users.SingleAsync(u => u.Id == VanUser);
        user.AssignedWarehouseCode = "VAN004";
        await _context.SaveChangesAsync();

        // What VAN005 was carrying, reported by a handset now signed in to a VAN004 account.
        var position = BuildPosition(quantity: 30m);
        position.Lines[0].Code = "YOG020";

        var response = await ReportAsync(position);

        Assert.True(response.Accepted);
        Assert.Equal("VAN004", response.WarehouseCode);
        Assert.Empty(_context.DailyStockSnapshots);
    }

    /// <summary>
    /// A snapshot the 07:00 read from SAP has already written is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_count_leaves_the_snapshot_SAP_wrote_as_it_was()
    {
        _context.DailyStockSnapshots.Add(new DailyStockSnapshotEntity
        {
            SnapshotDate = CapturedDay,
            WarehouseCode = "VAN006",
            Status = StockSnapshotStatus.Complete,
            ItemCount = 1,
            Items =
            [
                new DailyStockSnapshotItemEntity
                {
                    ItemCode = "CHE011",
                    WarehouseCode = "VAN006",
                    BatchNumber = "B2609"
                }.Opening(24m)
            ]
        });
        await _context.SaveChangesAsync();

        await ReportAsync(BuildPosition(quantity: 11m));

        var snapshot = await _context.DailyStockSnapshots.Include(s => s.Items).SingleAsync();
        Assert.Equal(1, snapshot.ItemCount);
        Assert.Equal(24m, snapshot.Items.Single().OriginalQuantity);
        Assert.Equal(24m, snapshot.Items.Single().AvailableQuantity);
    }

    /// <summary>
    /// The trading day in the reply is the handset's, not the server's — the handset logs it as the day
    /// it filed for.
    /// </summary>
    [Fact]
    public async Task The_trading_day_comes_from_the_handset()
    {
        var position = BuildPosition();
        position.CapturedAt = CapturedAtCat("23:40:00");

        var response = await ReportAsync(position);

        Assert.Equal(CapturedDay.ToString("yyyy-MM-dd"), response.TradingDate);
    }

    /// <summary>
    /// An empty count is still refused, so the contract handsets were built against does not move.
    /// </summary>
    [Fact]
    public async Task An_empty_count_is_refused()
    {
        var result = await BuildHandler().Handle(
            new ReportVanSalesStockPositionCommand(new VanSalesStockPositionRequest(), VanUser),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCompatibility.EmptyStockPosition", result.FirstError.Code);
        Assert.Empty(_context.DailyStockSnapshots);
    }

    /// <summary>
    /// An account with no van assigned has no warehouse to file against, and is told so rather than
    /// having a count filed against an empty code.
    /// </summary>
    [Fact]
    public async Task An_account_with_no_van_is_refused()
    {
        var user = await _context.Users.SingleAsync(u => u.Id == VanUser);
        user.AssignedWarehouseCode = null;
        user.AssignedWarehouseCodes = null;
        await _context.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new ReportVanSalesStockPositionCommand(BuildPosition(), VanUser),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCompatibility.MissingWarehouse", result.FirstError.Code);
    }
}
