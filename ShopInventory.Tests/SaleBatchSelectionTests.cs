using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A sale posted one-to-one must name the batches it issues from.
/// </summary>
/// <remarks>
/// SAP refuses a batch-managed line that names none — "Cannot add row without complete selection of
/// batch/serial numbers" — and refuses the whole document with it. Neither a till nor a handset
/// chooses a batch, and <c>DesktopSaleLineEntity</c> has no column to carry one, so both posting
/// services allocate before they post. Until they did, every till sale of a batch-managed item was
/// refused, retried five more times, and parked.
///
/// <para>
/// The other half of these is what a failure to allocate means. Two answers come back looking alike
/// and must not be treated alike: a warehouse that is short needs a person, and a warehouse that
/// could not be read needs another pass. Spending an attempt on the second turns a Service Layer
/// blip into a morning of manual posting.
/// </para>
/// </remarks>
public sealed class SaleBatchSelectionTests : IDisposable
{
    private static readonly DateTime TradingDate = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingSapClient _sap = new();

    public SaleBatchSelectionTests()
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
    public async Task A_till_sale_names_its_batches_on_the_invoice_it_sends_sap()
    {
        await GivenSaleAsync();

        var result = await TillService().PostPendingSalesAsync();

        Assert.Equal(1, result.Posted);

        var request = Assert.Single(_sap.Created);
        var line = Assert.Single(request.Lines!);

        // The assertion the whole fix is about: the document SAP receives carries a selection, and
        // it adds up to the line. A selection that does not add up is answered with -4014, which is
        // no better than none.
        Assert.NotNull(line.BatchNumbers);
        Assert.Equal("BATCH1", Assert.Single(line.BatchNumbers!).BatchNumber);
        Assert.Equal(line.Quantity, line.BatchNumbers!.Sum(batch => batch.Quantity));
    }

    [Fact]
    public async Task A_van_sale_names_its_batches_on_the_invoice_it_sends_sap()
    {
        await GivenSaleAsync(source: SaleSourceSystems.VanSales, warehouse: "VAN006");

        var result = await VanService().PostPendingSalesAsync(TradingDate);

        Assert.Equal(1, result.Posted);

        var request = Assert.Single(_sap.Created);
        var line = Assert.Single(request.Lines!);
        Assert.Equal("BATCH1", Assert.Single(line.BatchNumbers!).BatchNumber);
    }

    [Fact]
    public async Task A_warehouse_that_could_not_be_read_does_not_spend_an_attempt()
    {
        var sale = await GivenSaleAsync();

        var result = await TillService(SaleBatchAllocators.Unreadable()).PostPendingSalesAsync();

        Assert.Equal(1, result.Failed);
        Assert.Empty(_sap.Created);

        var refused = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);

        // Nothing was issued, so nothing durable may say one was. A sale left with PostIssuedAtUtc
        // set spends the rest of its budget asking SAP about an invoice that never existed.
        Assert.Null(refused.PostIssuedAtUtc);
        Assert.Equal(0, refused.PostingAttempts);
        Assert.Contains("could not be read", refused.LastPostingError);
    }

    [Fact]
    public async Task A_van_whose_stock_could_not_be_read_does_not_spend_an_attempt_either()
    {
        // The van route records its own failures rather than sharing the till's RecordFailure, so
        // the rule has to be pinned on both or it can drift on one.
        var sale = await GivenSaleAsync(source: SaleSourceSystems.VanSales, warehouse: "VAN006");

        var result = await VanService(SaleBatchAllocators.Unreadable()).PostPendingSalesAsync(TradingDate);

        Assert.Equal(1, result.Failed);
        Assert.Empty(_sap.Created);

        var refused = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Null(refused.PostIssuedAtUtc);
        Assert.Equal(0, refused.PostingAttempts);
    }

    [Fact]
    public async Task A_warehouse_that_is_short_spends_one_and_says_so()
    {
        var sale = await GivenSaleAsync();

        var result = await TillService(SaleBatchAllocators.Short()).PostPendingSalesAsync();

        Assert.Equal(1, result.Failed);
        Assert.Empty(_sap.Created);

        var refused = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);

        // Retrying cannot conjure stock, so this one is rationed and ends up in front of a person.
        Assert.Equal(1, refused.PostingAttempts);
        Assert.Null(refused.PostIssuedAtUtc);
        Assert.Contains("Insufficient", refused.LastPostingError);
    }

    [Fact]
    public void An_explicit_selection_is_not_overwritten()
    {
        // The transfer and credit-note paths name their own batches, having already validated them.
        // Allocation must leave those alone; it is filling a gap, not taking the decision over.
        var request = new CreateInvoiceRequest
        {
            Lines =
            [
                new CreateInvoiceLineRequest
                {
                    ItemCode = "CHE011",
                    Quantity = 2m,
                    WarehouseCode = "KEFGRS",
                    BatchNumbers = [new BatchNumberRequest { BatchNumber = "CHOSEN", Quantity = 2m }]
                }
            ]
        };

        InvoiceBatchAllocation.ApplyTo(request, [
            new AllocatedBatchLine
            {
                LineNumber = 1,
                ItemCode = "CHE011",
                WarehouseCode = "KEFGRS",
                IsBatchManaged = true,
                Batches = [new AllocatedBatch { BatchNumber = "FEFO", QuantityAllocated = 2m }]
            }
        ]);

        Assert.Equal("CHOSEN", Assert.Single(request.Lines![0].BatchNumbers!).BatchNumber);
    }

    [Fact]
    public void An_unreadable_warehouse_is_described_as_a_retry_not_a_shortage()
    {
        // SapFailureClassifier reads this string to decide whether the sale goes back on the queue,
        // and the words are what it reads. A message saying "insufficient" would park an outage as
        // though the shop had sold out.
        var message = InvoiceBatchAllocation.DescribeFailure(
            BatchAllocationResult.Failure(
            [
                new BatchValidationErrorDto
                {
                    ErrorCode = BatchValidationErrorCode.StockUnknown,
                    Message = "Stock could not be read from SAP. The reading is temporarily unavailable, not zero."
                }
            ]),
            "till sale KEFSHOP-01");

        Assert.Contains("could not be completed", message);
        Assert.True(SapFailureClassifier.IsTransient(new InvalidOperationException(message)));
        Assert.False(SapFailureClassifier.IsPermanentStockRejection(message));
    }

    private async Task<DesktopSaleEntity> GivenSaleAsync(
        string source = SaleSourceSystems.ShopTill,
        string warehouse = "KEFGRS")
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = "KEFSHOP-01-20260910-000123",
            SourceSystem = source,
            CardCode = "COR007",
            DocDate = TradingDate.Date,
            TotalAmount = 7.22m,
            VatAmount = 0.97m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            WarehouseCode = warehouse,
            AmountPaid = 0m,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    Quantity = 2m,
                    UnitPrice = 3.61m,
                    LineTotal = 7.22m,
                    WarehouseCode = warehouse
                }
            ]
        };

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        return sale;
    }

    private DesktopSalePostingService TillService(IBatchInventoryValidationService? allocator = null)
        => new(
            _context,
            _sap.Client,
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            allocator ?? SaleBatchAllocators.Holding(),
            SalePostGuards.Backed(_connection),
            Options.Create(new DesktopSalePostingSettings()),
            Options.Create(new SAPSettings()),
            NullLogger<DesktopSalePostingService>.Instance);

    private VanSalesEndOfDayPostingService VanService(IBatchInventoryValidationService? allocator = null)
        => new(
            _context,
            _sap.Client,
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            allocator ?? SaleBatchAllocators.Holding(),
            new StockLedger(_context, Options.Create(new DailyStockSettings()), NullLogger<StockLedger>.Instance),
            SalePostGuards.Backed(_connection),
            Options.Create(new VanSalesPostingSettings()),
            NullLogger<VanSalesEndOfDayPostingService>.Instance);

    private sealed class RecordingSapClient
    {
        public List<CreateInvoiceRequest> Created { get; } = [];

        private int _nextDocNum = 7000;

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) =>
                (object)Task.FromResult<Invoice?>(null),

            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Create((CreateInvoiceRequest)args![0]!),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Task<Invoice> Create(CreateInvoiceRequest request)
        {
            Created.Add(request);
            var docNum = _nextDocNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        }
    }
}
