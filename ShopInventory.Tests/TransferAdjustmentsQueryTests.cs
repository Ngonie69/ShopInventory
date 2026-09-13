using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransferAdjustments;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A till can read which transfers its stock ledger applied, document by document.
/// </summary>
/// <remarks>
/// The local stock route sums these into one figure per item per day, which cannot tell a transfer the
/// ledger applied from one SAP holds that never reached it. These rows can.
/// </remarks>
public sealed class TransferAdjustmentsQueryTests : IDisposable
{
    private const string Shop = "KEFSHOP";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public TransferAdjustmentsQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
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
    public async Task Returns_the_shops_rows_for_the_days_asked_in_the_order_they_were_applied()
    {
        await AddAsync(new DateTime(2026, 9, 11), "OLD", Shop, -1, new DateTime(2026, 9, 11, 9, 0, 0));
        await AddAsync(new DateTime(2026, 9, 12), "LATER", Shop, -12, new DateTime(2026, 9, 12, 14, 5, 0), docNum: 88021, destination: "KEFGRS");
        await AddAsync(new DateTime(2026, 9, 12), "EARLIER", Shop, 24, new DateTime(2026, 9, 12, 8, 30, 0), docNum: 88020, source: "KEFHQ");
        await AddAsync(new DateTime(2026, 9, 12), "ELSEWHERE", "KEFGRS", 12, new DateTime(2026, 9, 12, 14, 5, 0));
        await AddAsync(new DateTime(2026, 9, 14), "NEXT", Shop, 3, new DateTime(2026, 9, 14, 9, 0, 0));

        var result = await Handle(new DateTime(2026, 9, 12), new DateTime(2026, 9, 13));

        Assert.False(result.IsError);
        Assert.Equal(["EARLIER", "LATER"], result.Value.Select(row => row.ItemCode));

        var outgoing = result.Value[1];
        Assert.Equal(-12m, outgoing.Quantity);
        Assert.Equal("OUT", outgoing.Direction);
        Assert.Equal(88021, outgoing.TransferDocNum);
        Assert.Equal("KEFGRS", outgoing.DestinationWarehouse);
        Assert.Equal(new DateTime(2026, 9, 12), outgoing.SnapshotDate);
    }

    [Fact]
    public async Task The_time_applied_is_marked_as_UTC_so_a_till_can_convert_it()
    {
        await AddAsync(new DateTime(2026, 9, 12), "A", Shop, 1, new DateTime(2026, 9, 12, 6, 15, 0));

        var result = await Handle(new DateTime(2026, 9, 12), new DateTime(2026, 9, 12));

        Assert.Equal(DateTimeKind.Utc, Assert.Single(result.Value).DetectedAt.Kind);
    }

    [Fact]
    public async Task A_backwards_range_is_refused()
    {
        var result = await Handle(new DateTime(2026, 9, 13), new DateTime(2026, 9, 12));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_range_longer_than_a_quarter_is_refused()
    {
        var result = await Handle(new DateTime(2026, 1, 1), new DateTime(2026, 9, 1));

        Assert.True(result.IsError);
    }

    private Task<ErrorOr.ErrorOr<List<TransferAdjustmentDto>>> Handle(DateTime from, DateTime to) =>
        new GetTransferAdjustmentsHandler(_context).Handle(new GetTransferAdjustmentsQuery(Shop, from, to), default);

    private async Task AddAsync(
        DateTime snapshotDate,
        string item,
        string warehouse,
        decimal quantity,
        DateTime detectedAt,
        int? docNum = null,
        string? source = null,
        string? destination = null)
    {
        _context.StockTransferAdjustments.Add(new StockTransferAdjustmentEntity
        {
            SnapshotDate = snapshotDate,
            ItemCode = item,
            WarehouseCode = warehouse,
            AdjustmentQuantity = quantity,
            Direction = quantity > 0 ? "IN" : "OUT",
            TransferDocEntry = docNum is null ? null : docNum - 80000,
            TransferDocNum = docNum,
            SourceWarehouse = source,
            DestinationWarehouse = destination,
            DetectedAt = detectedAt,
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
