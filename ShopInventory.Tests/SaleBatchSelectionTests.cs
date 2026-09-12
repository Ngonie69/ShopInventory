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
    public async Task A_pass_reads_each_warehouse_once_and_allocates_from_what_is_left()
    {
        // The real allocator over a real read window, because the whole point is what the two do
        // together and a stub for either would prove neither.
        await GivenSaleAsync(reference: "KEFSHOP-01-20260910-000001", quantity: 2m);
        await GivenSaleAsync(reference: "KEFSHOP-01-20260910-000002", quantity: 2m);

        var sap = new CountingWarehouse(("B-EARLY", 3m, "2026-10-01"), ("B-LATE", 10m, "2026-12-01"));

        var result = await TillServiceOver(sap).PostPendingSalesAsync();

        Assert.Equal(2, result.Posted);

        // One reading for the pass, not one per sale. These are the reads that hang for minutes
        // while holding one of six process-wide Service Layer slots.
        Assert.Equal(1, sap.BatchReads);

        // And the second sale allocated from what the first left, not from the reading as it was
        // taken. B-EARLY held 3 and the first sale took 2, so the second gets the last of it and
        // the rest from B-LATE. Without the deduction it would have asked SAP for 2 of B-EARLY a
        // second time, and SAP would have refused the document.
        Assert.Equal(
            [("B-EARLY", 1m), ("B-LATE", 1m)],
            sap.Created[1].Lines![0].BatchNumbers!
                .Select(batch => (batch.BatchNumber, batch.Quantity))
                .ToArray());
    }

    [Fact]
    public async Task A_sale_that_did_not_post_is_not_taken_off_the_reading()
    {
        // A refusal SAP is certain about: nothing was created, so the warehouse still holds what the
        // reading says and the next sale must see all of it.
        await GivenSaleAsync(reference: "KEFSHOP-01-20260910-000001", quantity: 2m);
        await GivenSaleAsync(reference: "KEFSHOP-01-20260910-000002", quantity: 2m);

        var sap = new CountingWarehouse(("B-EARLY", 3m, "2026-10-01"), ("B-LATE", 10m, "2026-12-01"))
        {
            RefuseFirstPost = true
        };

        var result = await TillServiceOver(sap).PostPendingSalesAsync();

        Assert.Equal(1, result.Posted);
        Assert.Equal(1, result.Failed);

        // The sale that posted took B-EARLY from the top, because the refused one committed
        // nothing. Deducting for it would have pushed this sale onto B-LATE, and over a run of
        // refusals would have refused sales that were fine.
        var posted = Assert.Single(sap.Created).Lines![0].BatchNumbers!;
        Assert.Equal("B-EARLY", Assert.Single(posted).BatchNumber);
        Assert.Equal(2m, posted[0].Quantity);
    }

    [Fact]
    public void A_window_cannot_be_opened_twice_over()
    {
        // Nesting would give the inner window the outer readings and end them on the inner dispose.
        // Refused rather than quietly allowed, because that is a lifetime nobody wrote down.
        var service = RealAllocator(new CountingWarehouse());

        using var window = service.BeginSharedReadWindow();

        Assert.Throws<InvalidOperationException>(() => service.BeginSharedReadWindow());
    }

    [Fact]
    public async Task Readings_do_not_outlive_the_window()
    {
        var sap = new CountingWarehouse(("B-EARLY", 3m, "2026-10-01"));
        var service = RealAllocator(sap);

        using (service.BeginSharedReadWindow())
        {
            await service.GetAvailableBatchesAsync("CHE011", "KEFGRS");
            await service.GetAvailableBatchesAsync("CHE011", "KEFGRS");
        }

        await service.GetAvailableBatchesAsync("CHE011", "KEFGRS");

        // Two asks inside the window cost one read; the ask after it costs another. The service is
        // scoped to a request and may well be asked something else once the run is over.
        Assert.Equal(2, sap.BatchReads);
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
        string warehouse = "KEFGRS",
        string reference = "KEFSHOP-01-20260910-000123",
        decimal quantity = 2m)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
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
                    Quantity = quantity,
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

    /// <summary>The till pass over the real allocator, so the read window is the real one too.</summary>
    private DesktopSalePostingService TillServiceOver(CountingWarehouse sap)
        => new(
            _context,
            sap.Client,
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            RealAllocator(sap),
            SalePostGuards.Backed(_connection),
            DesktopCreditPosters.Idle(_context),
            Options.Create(new DesktopSalePostingSettings()),
            Options.Create(new SAPSettings()),
            NullLogger<DesktopSalePostingService>.Instance);

    private BatchInventoryValidationService RealAllocator(CountingWarehouse sap)
        => new(
            _context,
            sap.Client,
            StubProxy.Unused<IInventoryLockService>(),
            Options.Create(new SAPSettings()),
            NullLogger<BatchInventoryValidationService>.Instance);

    /// <summary>
    /// A batch-managed warehouse whose reads are counted, and which can refuse a post outright.
    /// </summary>
    private sealed class CountingWarehouse
    {
        private readonly (string Batch, decimal Quantity, string Expiry)[] _batches;
        private ISAPServiceLayerClient? _client;
        private bool _refused;
        private int _nextDocNum = 8000;

        public CountingWarehouse(params (string Batch, decimal Quantity, string Expiry)[] batches) =>
            _batches = batches;

        public int BatchReads { get; private set; }

        public bool RefuseFirstPost { get; init; }

        public List<CreateInvoiceRequest> Created { get; } = [];

        public ISAPServiceLayerClient Client =>
            _client ??= StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => Task.FromResult<Item?>(new Item
                {
                    ItemCode = "CHE011",
                    ManageBatchNumbers = "tYES",
                    ManageSerialNumbers = "tNO"
                }),

                nameof(ISAPServiceLayerClient.GetBatchNumbersForItemInWarehouseAsync) => ReadBatches(),

                nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) =>
                    (object)Task.FromResult<Invoice?>(null),

                nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Create((CreateInvoiceRequest)args![0]!),

                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<Invoice> Create(CreateInvoiceRequest request)
        {
            if (RefuseFirstPost && !_refused)
            {
                _refused = true;

                // The one refusal SAP is certain about, and so the one that proves nothing exists.
                return Task.FromException<Invoice>(
                    new SapRequestRejectedException(
                        "SAP refused to create the invoice: bad line.",
                        System.Net.HttpStatusCode.BadRequest,
                        "-5002"));
            }

            // Deep enough to survive the caller: the request object is reused per sale and the
            // allocation is written onto its lines, so a shallow record would read as the last one.
            Created.Add(new CreateInvoiceRequest
            {
                CardCode = request.CardCode,
                U_Van_saleorder = request.U_Van_saleorder,
                Lines = request.Lines?
                    .Select(line => new CreateInvoiceLineRequest
                    {
                        ItemCode = line.ItemCode,
                        Quantity = line.Quantity,
                        WarehouseCode = line.WarehouseCode,
                        BatchNumbers = line.BatchNumbers?
                            .Select(batch => new BatchNumberRequest
                            {
                                BatchNumber = batch.BatchNumber,
                                Quantity = batch.Quantity
                            })
                            .ToList()
                    })
                    .ToList()
            });

            var docNum = _nextDocNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        }

        private Task<List<BatchNumber>> ReadBatches()
        {
            BatchReads++;

            return Task.FromResult(_batches
                .Select(batch => new BatchNumber
                {
                    ItemCode = "CHE011",
                    BatchNum = batch.Batch,
                    Quantity = batch.Quantity,
                    ExpiryDate = batch.Expiry,
                    AdmissionDate = "2026-01-01"
                })
                .ToList());
        }
    }

    private DesktopSalePostingService TillService(IBatchInventoryValidationService? allocator = null)
        => new(
            _context,
            _sap.Client,
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            allocator ?? SaleBatchAllocators.Holding(),
            SalePostGuards.Backed(_connection),
            DesktopCreditPosters.Idle(_context),
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
