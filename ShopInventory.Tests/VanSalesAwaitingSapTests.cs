using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Common.Stock;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// What a van has sold that SAP's warehouse figure does not reflect yet — the figure the van handset now
/// takes off SAP's instead of every sale it made that day.
/// </summary>
/// <remarks>
/// The case this exists for is VAN004 on 2026-09-29. Invoice 781775 posted at 11:22 and took YOG008 from
/// 11 to 6 in SAP; at 12:57 the handset showed 1, because it subtracted the same 5 again. Once SAP has a
/// sale this answers nothing for it, and the handset reads SAP as it stands.
/// </remarks>
public sealed class VanSalesAwaitingSapTests : IDisposable
{
    private const string Van = "VAN004";
    private const string Item = "YOG008";
    private const int LookbackDays = 30;

    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesAwaitingSapTests()
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

    private Task<Dictionary<string, decimal>> AwaitingAsync(params string[] items) =>
        VanSalesAwaitingSap.ByItemAsync(
            _context, Van, items.Length == 0 ? [Item] : items, Now, LookbackDays, CancellationToken.None);

    // ---------------------------------------------------------------
    // Offline sales: uploaded, and waiting for the posting pass
    // ---------------------------------------------------------------

    [Fact]
    public async Task An_uploaded_sale_SAP_has_not_taken_counts()
    {
        await UploadAsync(5m, DesktopSaleConsolidationStatus.Pending);

        Assert.Equal(5m, (await AwaitingAsync())[Item]);
    }

    [Fact]
    public async Task A_sale_SAP_keeps_refusing_still_counts()
    {
        // The goods left the van whatever SAP said about the invoice.
        await UploadAsync(3m, DesktopSaleConsolidationStatus.Failed);

        Assert.Equal(3m, (await AwaitingAsync())[Item]);
    }

    [Fact]
    public async Task A_sale_SAP_took_before_the_figure_was_read_does_not_count()
    {
        // The 29 September case: posted well before the read, so SAP's figure already has it off.
        await UploadAsync(5m, DesktopSaleConsolidationStatus.Consolidated, postedAt: Now.AddMinutes(-95));

        Assert.Empty(await AwaitingAsync());
    }

    [Fact]
    public async Task A_sale_SAP_took_moments_ago_still_counts()
    {
        // The warehouse figure can be two minutes old, so it may not have this one off yet. Counting it
        // reads low for a few minutes; not counting it could sell the same goods twice.
        await UploadAsync(5m, DesktopSaleConsolidationStatus.Consolidated, postedAt: Now.AddSeconds(-90));

        Assert.Equal(5m, (await AwaitingAsync())[Item]);
    }

    [Fact]
    public async Task The_receipt_row_an_online_sale_leaves_is_not_counted_on_top_of_its_reservation()
    {
        await UploadAsync(5m, DesktopSaleConsolidationStatus.Consolidated, source: SaleSourceSystems.VanSalesOnline);
        await ReserveAsync(5m, ReservationStatus.Pending, queue: InvoiceQueueStatus.Pending);

        Assert.Equal(5m, (await AwaitingAsync())[Item]);
    }

    [Fact]
    public async Task A_sale_older_than_the_lookback_is_left_to_the_exception_centre()
    {
        await UploadAsync(5m, DesktopSaleConsolidationStatus.Pending, createdAt: Now.AddDays(-(LookbackDays + 1)));

        Assert.Empty(await AwaitingAsync());
    }

    // ---------------------------------------------------------------
    // Online sales: held by a reservation until the queue posts them
    // ---------------------------------------------------------------

    [Fact]
    public async Task An_online_sale_waiting_for_the_queue_counts()
    {
        await ReserveAsync(4m, ReservationStatus.Pending, queue: InvoiceQueueStatus.Pending);

        Assert.Equal(4m, (await AwaitingAsync())[Item]);
    }

    [Fact]
    public async Task An_online_sale_being_posted_counts()
    {
        await ReserveAsync(4m, ReservationStatus.Confirming);

        Assert.Equal(4m, (await AwaitingAsync())[Item]);
    }

    [Fact]
    public async Task An_online_sale_confirmed_moments_ago_counts_and_one_confirmed_earlier_does_not()
    {
        await ReserveAsync(4m, ReservationStatus.Confirmed, confirmedAt: Now.AddSeconds(-60));
        await ReserveAsync(7m, ReservationStatus.Confirmed, confirmedAt: Now.AddMinutes(-20));

        Assert.Equal(4m, (await AwaitingAsync())[Item]);
    }

    [Fact]
    public async Task A_queued_sale_the_queue_settled_moments_ago_counts_and_one_settled_earlier_does_not()
    {
        // Consolidation completes the queue entry and leaves the reservation Pending.
        await ReserveAsync(2m, ReservationStatus.Pending, queue: InvoiceQueueStatus.Completed, processedAt: Now.AddSeconds(-30));
        await ReserveAsync(9m, ReservationStatus.Pending, queue: InvoiceQueueStatus.Completed, processedAt: Now.AddHours(-2));

        Assert.Equal(2m, (await AwaitingAsync())[Item]);
    }

    [Fact]
    public async Task A_cancelled_or_expired_reservation_does_not_count()
    {
        await ReserveAsync(4m, ReservationStatus.Cancelled);
        await ReserveAsync(6m, ReservationStatus.Expired);

        Assert.Empty(await AwaitingAsync());
    }

    // ---------------------------------------------------------------
    // Scope
    // ---------------------------------------------------------------

    [Fact]
    public async Task Offline_and_online_sales_of_the_same_item_add_up()
    {
        await UploadAsync(5m, DesktopSaleConsolidationStatus.Pending);
        await ReserveAsync(4m, ReservationStatus.Pending, queue: InvoiceQueueStatus.Pending);

        Assert.Equal(9m, (await AwaitingAsync())[Item]);
    }

    [Fact]
    public async Task Another_van_and_an_item_not_asked_about_are_left_out()
    {
        await UploadAsync(5m, DesktopSaleConsolidationStatus.Pending, warehouse: "VAN005");
        await UploadAsync(3m, DesktopSaleConsolidationStatus.Pending, item: "YOG004");

        var awaiting = await AwaitingAsync(Item);

        Assert.Empty(awaiting);
        Assert.Equal(3m, (await AwaitingAsync("YOG004"))["YOG004"]);
    }

    // ---------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------

    private async Task UploadAsync(
        decimal quantity,
        DesktopSaleConsolidationStatus status,
        DateTime? postedAt = null,
        DateTime? createdAt = null,
        string source = SaleSourceSystems.VanSales,
        string warehouse = Van,
        string item = Item)
    {
        var reference = $"{warehouse}-INV-{Guid.NewGuid():N}"[..28];

        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = "VAN008",
            DocDate = Now.Date,
            TotalAmount = 10m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = status,
            PostedAt = postedAt,
            SapDocNum = postedAt is null ? null : 781775,
            WarehouseCode = warehouse,
            CreatedAt = createdAt ?? Now.AddHours(-2),
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = item,
                    Quantity = quantity,
                    UnitPrice = 2m,
                    LineTotal = quantity * 2m,
                    WarehouseCode = warehouse
                }
            ]
        });

        await _context.SaveChangesAsync();
    }

    private async Task ReserveAsync(
        decimal quantity,
        string status,
        DateTime? confirmedAt = null,
        InvoiceQueueStatus? queue = null,
        DateTime? processedAt = null)
    {
        var reservation = new StockReservationEntity
        {
            ExternalReferenceId = $"VAN004-INV-{Guid.NewGuid():N}"[..28],
            SourceSystem = "VanSales",
            CardCode = "VAN008",
            Status = status,
            CreatedAt = Now.AddMinutes(-30),
            // Past: whether it holds is the queue's question, not the clock's, for a queued sale.
            ExpiresAt = queue is null ? Now.AddMinutes(30) : Now.AddMinutes(-1),
            ConfirmedAt = confirmedAt,
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = Item,
                    WarehouseCode = Van,
                    ReservedQuantity = quantity,
                    OriginalQuantity = quantity
                }
            ]
        };

        _context.StockReservations.Add(reservation);

        if (queue is { } queueStatus)
        {
            _context.InvoiceQueue.Add(new InvoiceQueueEntity
            {
                ReservationId = reservation.ReservationId,
                ExternalReference = reservation.ExternalReferenceId,
                SourceSystem = "VanSales",
                CustomerCode = "VAN008",
                TotalAmount = 10m,
                Currency = "USD",
                WarehouseCode = Van,
                Status = queueStatus,
                CreatedAt = Now.AddMinutes(-30),
                ProcessedAt = processedAt
            });
        }

        await _context.SaveChangesAsync();
    }
}
