using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility.Commands.ReportVanSalesStockPosition;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A van counting what it is carrying, and filing that count as its own stock snapshot.
///
/// The portal's van stock report reads nothing but these snapshots, and until this route existed a van
/// wrote none: the job that fills them reads SAP — a day behind for a van, whose sales are still queued
/// on the handset — and only visits the warehouses named in its configured list. A van absent from that
/// list produced an empty report page rather than an error, which is the failure mode that hides.
///
/// The invariant worth the most here is that the first count of a day wins. The report compares a
/// morning's opening position against the previous morning's less what sold in between, so a position
/// that can be rewritten at lunchtime would absorb exactly the variance the report exists to surface.
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

    private static VanSalesStockPositionRequest BuildPosition(decimal quantity = 24m) => new()
    {
        CapturedAt = "2026-08-10T05:42:11",
        ClientReference = "VAN006-STK-20260810-AAA111",
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
    /// The count lands as a complete snapshot for the van's own warehouse, on the day the handset
    /// counted.
    /// </summary>
    [Fact]
    public async Task A_reported_position_becomes_the_vans_snapshot_for_the_day()
    {
        var response = await ReportAsync(BuildPosition());

        Assert.True(response.Accepted);
        Assert.False(response.Duplicate);
        Assert.Equal("VAN006", response.WarehouseCode);
        Assert.Equal("2026-08-10", response.TradingDate);

        var snapshot = await _context.DailyStockSnapshots.Include(s => s.Items).SingleAsync();
        Assert.Equal("VAN006", snapshot.WarehouseCode);
        Assert.Equal(new DateTime(2026, 8, 10), snapshot.SnapshotDate);

        // Complete, or the report will not read it: it filters snapshots on this status.
        Assert.Equal(StockSnapshotStatus.Complete, snapshot.Status);

        var item = Assert.Single(snapshot.Items);
        Assert.Equal("CHE011", item.ItemCode);
        Assert.Equal("B2609", item.BatchNumber);
        Assert.Equal(new DateTime(2026, 9, 30), item.ExpiryDate);

        // OriginalQuantity is the morning figure the report reads. AvailableQuantity is the working one
        // the desktop paths decrement; nothing decrements it for a van, which is why the report reads
        // the other. Both start at what was counted.
        Assert.Equal(24m, item.OriginalQuantity);
        Assert.Equal(24m, item.AvailableQuantity);
    }

    /// <summary>
    /// The trading day is the handset's, not the server's — the same rule the sales upload follows. A
    /// van that counts at 05:42 files against that morning wherever the server happens to be.
    /// </summary>
    [Fact]
    public async Task The_trading_day_comes_from_the_handset()
    {
        var position = BuildPosition();
        position.CapturedAt = "2026-08-10T23:40:00";

        await ReportAsync(position);

        var snapshot = await _context.DailyStockSnapshots.SingleAsync();
        Assert.Equal(new DateTime(2026, 8, 10), snapshot.SnapshotDate);
    }

    /// <summary>
    /// A handset that lost the reply re-sends, and the count already held is the one kept.
    /// </summary>
    /// <remarks>
    /// The second count is not merely ignored — it must be answered as a success, or the handset
    /// retries it forever. And the held figure must not move: it is the opening position the next
    /// morning's variance is measured against.
    /// </remarks>
    [Fact]
    public async Task A_resent_position_is_a_duplicate_and_the_first_count_stands()
    {
        await ReportAsync(BuildPosition(quantity: 24m));

        var second = await ReportAsync(BuildPosition(quantity: 11m));

        Assert.True(second.Accepted);
        Assert.True(second.Duplicate);

        var snapshot = await _context.DailyStockSnapshots.Include(s => s.Items).SingleAsync();
        Assert.Equal(24m, snapshot.Items.Single().OriginalQuantity);
    }

    /// <summary>
    /// An empty count is refused rather than filed.
    /// </summary>
    /// <remarks>
    /// A van genuinely carrying nothing and a handset whose ledger failed to load report exactly the
    /// same thing, and only one of them is true. Filing it would open the day at zero and report the
    /// whole load as a variance the next morning — a number nobody would read as a bug.
    /// </remarks>
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

    /// <summary>
    /// A line with no expiry is not a line that expired at the epoch. The expiry view lists what is
    /// close to going off, and a guessed date puts a good batch on that list.
    /// </summary>
    [Fact]
    public async Task A_line_with_no_expiry_records_none()
    {
        var position = BuildPosition();
        position.Lines[0].ExpiryDate = null;

        await ReportAsync(position);

        var snapshot = await _context.DailyStockSnapshots.Include(s => s.Items).SingleAsync();
        Assert.Null(snapshot.Items.Single().ExpiryDate);
    }
}
