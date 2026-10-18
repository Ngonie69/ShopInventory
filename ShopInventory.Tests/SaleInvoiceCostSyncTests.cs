using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the copy of SAP's booked invoice cost the sales reports read their gross profit from: every
/// kind of posted sale's invoice is copied once, an invoice SAP has no lines for is not asked about
/// again, a failing SAP leaves nothing half-copied, and the reports read the copy rather than SAP.
/// </summary>
public sealed class SaleInvoiceCostSyncTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 9, 1);
    private const string Shop = "KEFSHOP";
    private const string Van = "VAN010";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly FakeSapReader _sap = new();

    public SaleInvoiceCostSyncTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = NewContext();
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = _adminId,
            Username = "admin",
            PasswordHash = "x",
            Role = ApplicationRoles.Admin,
            IsActive = true,
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Every_kind_of_posted_sale_is_copied_once_and_read_back_by_invoice()
    {
        var consolidation = new SaleConsolidationEntity { CardCode = "CIS006", ConsolidationDate = Day, SapDocEntry = 520, SaleCount = 2, TotalAmount = 60m };
        _context.DesktopSales.AddRange(
            CounterSale("S1", 501, ("BREAD", 10, 100m)),
            CounterSale("S2", null, ("MILK", 4, 40m), consolidation: consolidation),
            CounterSale("S3", null, ("MILK", 2, 20m), consolidation: consolidation),
            // Not in SAP yet: nothing to copy.
            CounterSale("S4", null, ("JAM", 1, 9m)));
        _context.StockReservations.Add(OnlineVanSale("ON-1", 702, "CHE011", 3, 30m));
        await _context.SaveChangesAsync();

        _sap.Lines.AddRange(
        [
            new SaleInvoiceLineCost(501, "BREAD", Shop, 10m, 100m, 30m),
            new SaleInvoiceLineCost(520, "MILK", Shop, 6m, 60m, 9m),
            // Raised in SAP directly from the shop, inside the range read: not one of these sales.
            new SaleInvoiceLineCost(510, "BREAD", Shop, 50m, 500m, 400m),
            new SaleInvoiceLineCost(702, "CHE011", Van, 3m, 30m, 7m),
        ]);

        Assert.Equal(3, await SyncAsync());

        // One read per warehouse, for exactly the invoices the sales were posted as.
        Assert.Equal(2, _sap.Reads.Count);
        Assert.Contains(_sap.Reads, read => read.Warehouse == Shop && read.DocEntries.SequenceEqual([501, 520]));
        Assert.Contains(_sap.Reads, read => read.Warehouse == Van && read.DocEntries.SequenceEqual([702]));

        var stored = await new StoredSaleInvoiceCostReader(NewContext())
            .ReadAsync([Shop, Van], [501, 510, 520, 702], CancellationToken.None);
        Assert.Equal(
            [(501, "BREAD", 30m), (520, "MILK", 9m), (702, "CHE011", 7m)],
            stored.OrderBy(line => line.DocEntry).Select(line => (line.DocEntry, line.ItemCode, line.GrossProfit)));

        // Already copied: a second run asks SAP for nothing.
        _sap.Reads.Clear();
        Assert.Equal(0, await SyncAsync());
        Assert.Empty(_sap.Reads);
    }

    [Fact]
    public async Task An_invoice_SAP_has_no_lines_for_is_kept_as_read_and_carries_no_cost()
    {
        // Cancelled in SAP: the reader leaves its lines out.
        _context.DesktopSales.Add(CounterSale("S1", 501, ("BREAD", 10, 100m)));
        await _context.SaveChangesAsync();

        Assert.Equal(1, await SyncAsync());
        Assert.Empty(await new StoredSaleInvoiceCostReader(NewContext()).ReadAsync([Shop], [501], CancellationToken.None));

        _sap.Reads.Clear();
        await SyncAsync();
        Assert.Empty(_sap.Reads);
    }

    [Fact]
    public async Task SAP_failing_copies_nothing_stops_the_run_and_is_tried_again_next_time()
    {
        // Three warehouses, so three reads are planned.
        _context.DesktopSales.AddRange(
            CounterSale("S1", 501, ("BREAD", 10, 100m)),
            CounterSale("S2", 601, ("BREAD", 10, 100m), warehouse: "DEPOT1"),
            CounterSale("S3", 701, ("BREAD", 10, 100m), warehouse: "DEPOT2"));
        await _context.SaveChangesAsync();
        _sap.Failure = new TimeoutException("SAP did not answer");

        Assert.Equal(0, await SyncAsync());
        Assert.Equal(2, _sap.Reads.Count);
        Assert.Equal(0, await NewContext().SaleInvoiceCosts.CountAsync());

        _sap.Failure = null;
        _sap.Reads.Clear();
        Assert.Equal(3, await SyncAsync());
    }

    [Fact]
    public void Reads_stay_short_and_the_newest_go_first()
    {
        var pending = Enumerable.Range(0, 250).Select(i => new SaleInvoiceCostSyncService.PendingInvoice(10_000 + i, Shop))
            .Append(new SaleInvoiceCostSyncService.PendingInvoice(5_000, Shop))
            .Append(new SaleInvoiceCostSyncService.PendingInvoice(20_000, Van));

        var reads = SaleInvoiceCostSyncService.PlanReads(pending).ToList();

        Assert.Equal(
            [(Van, 20_000, 20_000), (Shop, 10_200, 10_249), (Shop, 10_000, 10_199), (Shop, 5_000, 5_000)],
            reads.Select(read => (read.WarehouseCode, read.DocEntries[0], read.DocEntries[^1])));
        Assert.All(reads, read =>
        {
            Assert.True(read.DocEntries.Length <= SaleInvoiceCostSyncService.MaxInvoicesPerRead);
            Assert.True(read.DocEntries[^1] - read.DocEntries[0] <= SaleInvoiceCostSyncService.MaxDocEntrySpan);
        });
    }

    [Fact]
    public async Task The_sales_analysis_takes_its_gross_profit_from_the_copy()
    {
        _context.DesktopSales.Add(CounterSale("S1", 501, ("BREAD", 10, 100m)));
        await _context.SaveChangesAsync();
        _sap.Lines.Add(new SaleInvoiceLineCost(501, "BREAD", Shop, 10m, 100m, 30m));
        await SyncAsync();

        var result = await new GetDesktopSalesAnalysisHandler(
                _context, new RecordingAuditService(), new StoredSaleInvoiceCostReader(NewContext()))
            .Handle(new GetDesktopSalesAnalysisQuery(_adminId, Day, Day, IncludeMargin: true), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(result.Value.Margin!.Available);
        var bread = result.Value.Currencies.Single().TopItems.Single();
        Assert.Equal(30m, bread.GrossProfit);
        Assert.Equal(30.0m, bread.MarginPercent);
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private Task<int> SyncAsync() =>
        new SaleInvoiceCostSyncService(NewContext(), _sap, NullLogger<SaleInvoiceCostSyncService>.Instance)
            .SyncAsync(CancellationToken.None);

    private DesktopSaleEntity CounterSale(
        string reference,
        int? sapDocEntry,
        (string Code, int Quantity, decimal Net) line,
        string warehouse = Shop,
        SaleConsolidationEntity? consolidation = null) => new()
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = "CIS006",
            CardName = "Counter",
            WarehouseCode = warehouse,
            DocDate = Day,
            TotalAmount = line.Net,
            AmountPaid = line.Net,
            Currency = "USD",
            PaymentMethod = "Cash",
            CreatedBy = _adminId.ToString(),
            CreatedAt = DateTime.SpecifyKind(Day.AddHours(8), DateTimeKind.Utc),
            SapDocEntry = sapDocEntry,
            Consolidation = consolidation,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = line.Code,
                    ItemDescription = line.Code,
                    Quantity = line.Quantity,
                    UnitPrice = line.Net / line.Quantity,
                    LineTotal = line.Net,
                    WarehouseCode = warehouse,
                }
            ],
        };

    private StockReservationEntity OnlineVanSale(string reference, int sapDocEntry, string item, int quantity, decimal net)
    {
        var at = DateTime.SpecifyKind(Day.AddHours(8), DateTimeKind.Utc);
        return new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = Van,
            TotalValue = net,
            Currency = "USD",
            PaymentMethod = "Cash",
            Status = ReservationStatus.Confirmed,
            CreatedAt = at,
            ExpiresAt = at.AddHours(1),
            ConfirmedAt = at,
            SAPDocEntry = sapDocEntry,
            CreatedBy = _adminId.ToString(),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0,
                    ItemCode = item,
                    ItemDescription = item,
                    OriginalQuantity = quantity,
                    ReservedQuantity = quantity,
                    UoMCode = "EA",
                    WarehouseCode = Van,
                    UnitPrice = net / quantity,
                    LineTotal = net
                }
            ]
        };
    }

    /// <summary>SAP's range read: one warehouse's lines on the invoices from the lowest to the highest asked for.</summary>
    private sealed class FakeSapReader : ISapSaleInvoiceCostReader
    {
        public List<SaleInvoiceLineCost> Lines { get; } = [];

        public List<(string Warehouse, int[] DocEntries)> Reads { get; } = [];

        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
            IReadOnlyCollection<string> warehouseCodes, IReadOnlyCollection<int> docEntries, CancellationToken cancellationToken)
        {
            Reads.Add((warehouseCodes.Single(), docEntries.ToArray()));
            if (Failure is not null)
            {
                throw Failure;
            }

            var (start, end) = (docEntries.Min(), docEntries.Max());
            return Task.FromResult<IReadOnlyList<SaleInvoiceLineCost>>(Lines
                .Where(line => warehouseCodes.Contains(line.WarehouseCode)
                               && line.DocEntry >= start && line.DocEntry <= end)
                .ToList());
        }
    }
}
