using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What happens to an invoice when SAP cannot say what is in stock.
/// </summary>
/// <remarks>
/// Three guards used to swallow a failed stock read and let the document through, each explaining
/// in a comment that SAP would validate instead. SAP only refuses an issue that takes stock below
/// zero when <b>Block Negative Inventory</b> is enabled, and nothing in this codebase has ever read
/// that flag — so on a company where it is off, every failed read was a negative quantity waiting
/// to happen. The reads in question are the ones already known to hang for minutes and starve the
/// six-slot Service Layer pool, so this was not a rare path.
///
/// <para>
/// Two things are pinned here, and the second is easy to lose in a later edit. The first is that a
/// failed read now refuses. The second is that it refuses in the right <i>words</i>: an unreadable
/// warehouse must not be classified as a shortage, or an outage gets filed for a human to go and
/// count a shelf that is fine.
/// </para>
/// </remarks>
public sealed class StockGuardFailClosedTests
{
    private const string Item = "CHE011";
    private const string Warehouse = "KEFSHOP";

    // ---------------------------------------------------------------
    // The guard itself
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_line_whose_stock_cannot_be_read_is_refused()
    {
        await using var context = InMemoryContext();
        var service = CreateService(context, StockReadThrows());

        var result = await service.ValidateAndAllocateBatchesAsync(OneLineInvoice());

        Assert.False(result.IsValid);
        Assert.Contains(result.ValidationErrors, e => e.ErrorCode == BatchValidationErrorCode.StockUnknown);
    }

    [Fact]
    public async Task An_unreadable_warehouse_is_not_reported_as_a_shortage()
    {
        await using var context = InMemoryContext();
        var service = CreateService(context, StockReadThrows());

        var result = await service.ValidateAndAllocateBatchesAsync(OneLineInvoice());
        var message = string.Join(" ", result.ValidationErrors.Select(e => e.Message));

        // The distinction this whole change exists to preserve. "Insufficient" would send someone
        // to count stock; the warehouse is fine and SAP is not.
        Assert.DoesNotContain("Insufficient", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be read", message, StringComparison.OrdinalIgnoreCase);

        // And nothing suggests cutting the document down.
        var suggestions = string.Join(" ", result.Suggestions);
        Assert.DoesNotContain("Reduce", suggestions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry", suggestions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_refusal_reads_as_transient_and_not_as_a_permanent_shortage()
    {
        await using var context = InMemoryContext();
        var service = CreateService(context, StockReadThrows());

        var result = await service.ValidateAndAllocateBatchesAsync(OneLineInvoice());
        var message = result.ValidationErrors.First(e => e.ErrorCode == BatchValidationErrorCode.StockUnknown).Message;

        // These two classifications decide where the work goes next. Permanent parks it in front of
        // a person; transient puts it back on the queue, which is right for a read that failed.
        Assert.False(SapFailureClassifier.IsPermanentStockRejection(message));
        Assert.True(SapFailureClassifier.IsTransient(new Exception(message), CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_request_is_not_reported_as_unreadable_stock()
    {
        await using var context = InMemoryContext();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var service = CreateService(context, StockReadThrows(new OperationCanceledException()));

        // The caller hung up. That is not an outage and must not be recorded as one.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ValidateAndAllocateBatchesAsync(OneLineInvoice(), cancellationToken: cancellation.Token));
    }

    // ---------------------------------------------------------------
    // The escape hatch
    // ---------------------------------------------------------------

    [Fact]
    public async Task With_the_guard_switched_off_the_line_passes_but_is_logged()
    {
        await using var context = InMemoryContext();
        var logger = new CapturingLogger<BatchInventoryValidationService>();
        var service = CreateService(
            context,
            StockReadThrows(),
            settings: new SAPSettings { StockGuardFailClosed = false },
            logger: logger);

        var result = await service.ValidateAndAllocateBatchesAsync(OneLineInvoice());

        Assert.True(result.IsValid);

        // Invoicing blind is a deliberate decision, so it leaves a trace naming what was let
        // through. A silent bypass would be the old behaviour with a setting attached.
        Assert.Contains(
            logger.AtOrAbove(LogLevel.Warning),
            entry => entry.Message.Contains("without a stock check") && entry.Message.Contains(Item));
    }

    [Fact]
    public void The_guard_is_closed_unless_somebody_opens_it()
    {
        Assert.True(new SAPSettings().StockGuardFailClosed);
    }

    // ---------------------------------------------------------------
    // The third site: the client's own whole-warehouse check
    // ---------------------------------------------------------------

    [Fact]
    public void An_unread_warehouse_reports_itself_rather_than_reading_as_zero_stock()
    {
        // SAPServiceLayerClient.ValidateStockAvailabilityAsync builds this row when the warehouse
        // read throws, where it used to `continue` and validate nothing at all. The row carries the
        // whole reason it exists in Message, which is what the caller shows.
        var error = new StockValidationError
        {
            ItemCode = Item,
            WarehouseCode = Warehouse,
            RequestedQuantity = 12,
            AvailableQuantity = 0,
            StockReadFailed = true,
            ReadFailureReason = "TimeoutException: SAP stock read exceeded its 60-second budget"
        };

        Assert.DoesNotContain("Insufficient", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be read", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(SapFailureClassifier.IsPermanentStockRejection(error.Message));

        // An ordinary shortage still reads as one.
        var shortage = new StockValidationError
        {
            ItemCode = Item,
            WarehouseCode = Warehouse,
            RequestedQuantity = 12,
            AvailableQuantity = 3
        };
        Assert.Contains("Insufficient stock", shortage.Message);
        Assert.True(SapFailureClassifier.IsPermanentStockRejection(shortage.Message));
    }

    // ---------------------------------------------------------------
    // A working read still behaves
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_readable_warehouse_with_enough_stock_still_passes()
    {
        await using var context = InMemoryContext();
        var service = CreateService(context, StockReadReturns(inStock: 50, committed: 0));

        var result = await service.ValidateAndAllocateBatchesAsync(OneLineInvoice());

        Assert.True(result.IsValid);
        Assert.Empty(result.ValidationErrors);
    }

    [Fact]
    public async Task A_readable_warehouse_that_is_short_is_still_a_shortage_not_an_outage()
    {
        await using var context = InMemoryContext();
        var service = CreateService(context, StockReadReturns(inStock: 4, committed: 0));

        var result = await service.ValidateAndAllocateBatchesAsync(OneLineInvoice());

        Assert.False(result.IsValid);
        Assert.Contains(result.ValidationErrors, e => e.ErrorCode == BatchValidationErrorCode.InsufficientTotalStock);
        Assert.DoesNotContain(result.ValidationErrors, e => e.ErrorCode == BatchValidationErrorCode.StockUnknown);
    }

    // ---------------------------------------------------------------
    // The batch read, which has no second source
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_batch_read_that_failed_is_not_reported_as_an_empty_warehouse()
    {
        await using var context = InMemoryContext();
        var service = CreateService(context, BatchManagedItemWhoseBatchReadThrows());

        var result = await service.ValidateAndAllocateBatchesAsync(OneLineInvoice());

        Assert.False(result.IsValid);

        // GetAvailableBatchesAsync answers a failed read with an empty list, which is a reasonable
        // answer to give an operator browsing batches and the wrong one to allocate from: per-
        // warehouse batch quantities live only in OBTQ and there is no second source to fall back
        // on. Read as zero, this refuses the sale and sends somebody to count a full shelf.
        Assert.Contains(result.ValidationErrors, e => e.ErrorCode == BatchValidationErrorCode.StockUnknown);
        Assert.DoesNotContain(result.ValidationErrors, e => e.ErrorCode == BatchValidationErrorCode.InsufficientTotalStock);

        var message = result.ValidationErrors.First().Message;
        Assert.False(SapFailureClassifier.IsPermanentStockRejection(message));
        Assert.True(SapFailureClassifier.IsTransient(new Exception(message), CancellationToken.None));
    }

    [Fact]
    public async Task A_batch_managed_line_is_allocated_from_what_the_warehouse_holds()
    {
        await using var context = InMemoryContext();
        var service = CreateService(context, BatchManagedItemHolding(("B-EARLY", 5m), ("B-LATE", 20m)));

        var result = await service.ValidateAndAllocateBatchesAsync(OneLineInvoice());

        Assert.True(result.IsValid);

        var line = Assert.Single(result.AllocatedLines);
        Assert.Equal(12m, line.Batches.Sum(batch => batch.QuantityAllocated));

        // FEFO: the batch expiring first is emptied before the next is touched.
        Assert.Equal("B-EARLY", line.Batches[0].BatchNumber);
        Assert.Equal(5m, line.Batches[0].QuantityAllocated);
    }

    // ---------------------------------------------------------------
    // One reading per warehouse per pass
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_warehouse_is_read_once_a_pass_however_many_lines_name_it()
    {
        await using var context = InMemoryContext();
        var sap = new CountingBatchClient(("B1", 100m));
        var service = CreateService(context, sap.Client);

        // Every line naming its own batches is validated line by line, and each line used to read
        // the warehouse again on top of the one read the aggregate check makes. These reads are the
        // ones known to hang for minutes while holding one of six process-wide Service Layer slots,
        // so four of them to answer one question is the whole cost this memo removes.
        var result = await service.ValidateAndAllocateBatchesAsync(ThreeLinesNamingBatches());

        Assert.True(result.IsValid, string.Join("; ", result.ValidationErrors.Select(e => e.Message)));
        Assert.Equal(1, sap.BatchReads);
    }

    [Fact]
    public async Task A_second_pass_reads_the_warehouse_again()
    {
        await using var context = InMemoryContext();
        var sap = new CountingBatchClient(("B1", 100m));
        var service = CreateService(context, sap.Client);

        await service.ValidateAndAllocateBatchesAsync(ThreeLinesNamingBatches());
        await service.ValidateAndAllocateBatchesAsync(ThreeLinesNamingBatches());

        // The boundary the memo must not cross, in both directions. PrePostValidationAsync runs a
        // second pass under the inventory locks precisely so that it reads afresh; and the till job
        // posts each sale to SAP before allocating the next, so a reading carried between passes
        // would hand the second sale stock the first has already taken.
        Assert.Equal(2, sap.BatchReads);
    }

    [Fact]
    public async Task A_failed_read_is_not_retried_within_the_pass()
    {
        await using var context = InMemoryContext();
        var sap = new CountingBatchClient(failure: new TimeoutException("SAP batch read exceeded its budget."));
        var service = CreateService(context, sap.Client);

        var result = await service.ValidateAndAllocateBatchesAsync(ThreeLinesNamingBatches());

        // During an outage a twenty-line document would otherwise spend twenty hung reads to reach
        // the verdict the first one already gave.
        Assert.Equal(1, sap.BatchReads);
        Assert.Contains(result.ValidationErrors, e => e.ErrorCode == BatchValidationErrorCode.StockUnknown);
    }

    [Fact]
    public async Task Two_callers_in_one_pass_do_not_see_each_other_edits()
    {
        await using var context = InMemoryContext();
        var sap = new CountingBatchClient(("EARLY", 5m), ("LATE", 20m));
        var service = CreateService(context, sap.Client);

        var fefo = await service.GetAvailableBatchesAsync(Item, Warehouse);
        fefo[0].AvailableQuantity = -999m;

        // AvailableBatchDto is mutable and GetAvailableBatchesAsync writes IsRecommended on what it
        // returns, so the memo hands out fresh instances rather than its own.
        var second = await service.GetAvailableBatchesAsync(Item, Warehouse);

        Assert.Equal(1, sap.BatchReads);
        Assert.Equal(5m, second[0].AvailableQuantity);
    }

    [Fact]
    public async Task The_memo_holds_one_reading_and_each_caller_sorts_it_for_itself()
    {
        await using var context = InMemoryContext();

        // Admission order is the reverse of expiry order, so the two strategies disagree and a memo
        // that had cached somebody sorted list would be caught out.
        var sap = new CountingBatchClient(
            ("EXPIRES-LAST", 10m, "2026-12-01", "2026-01-01"),
            ("EXPIRES-FIRST", 10m, "2026-10-01", "2026-06-01"));
        var service = CreateService(context, sap.Client);

        var fefo = await service.GetAvailableBatchesAsync(Item, Warehouse, BatchAllocationStrategy.FEFO);
        var fifo = await service.GetAvailableBatchesAsync(Item, Warehouse, BatchAllocationStrategy.FIFO);

        Assert.Equal(1, sap.BatchReads);
        Assert.Equal("EXPIRES-FIRST", fefo[0].BatchNumber);
        Assert.Equal("EXPIRES-LAST", fifo[0].BatchNumber);
    }

    /// <summary>Three lines on one item and warehouse, each naming its own batch.</summary>
    private static CreateInvoiceRequest ThreeLinesNamingBatches() => new()
    {
        CardCode = "C-1",
        DocCurrency = "USD",
        Lines = Enumerable.Range(0, 3)
            .Select(_ => new CreateInvoiceLineRequest
            {
                ItemCode = Item,
                Quantity = 2,
                UnitPrice = 5m,
                WarehouseCode = Warehouse,
                BatchNumbers = [new BatchNumberRequest { BatchNumber = "B1", Quantity = 2 }]
            })
            .ToList()
    };

    /// <summary>A batch-managed item whose warehouse read is counted.</summary>
    private sealed class CountingBatchClient
    {
        private readonly (string Batch, decimal Quantity, string Expiry, string Admission)[] _batches;
        private readonly Exception? _failure;

        public CountingBatchClient(params (string Batch, decimal Quantity)[] batches)
            : this(null, batches.Select(b => (b.Batch, b.Quantity, "2026-12-01", "2026-01-01")).ToArray())
        {
        }

        public CountingBatchClient(params (string Batch, decimal Quantity, string Expiry, string Admission)[] batches)
            : this(null, batches)
        {
        }

        public CountingBatchClient(Exception failure)
            : this(failure, [])
        {
        }

        private CountingBatchClient(
            Exception? failure,
            (string Batch, decimal Quantity, string Expiry, string Admission)[] batches)
        {
            _failure = failure;
            _batches = batches;
        }

        public int BatchReads { get; private set; }

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => Task.FromResult<Item?>(new Item
            {
                ItemCode = Item,
                ManageBatchNumbers = "tYES",
                ManageSerialNumbers = "tNO"
            }),
            nameof(ISAPServiceLayerClient.GetBatchNumbersForItemInWarehouseAsync) => Read(),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Task<List<BatchNumber>> Read()
        {
            BatchReads++;

            if (_failure is not null)
            {
                return Task.FromException<List<BatchNumber>>(_failure);
            }

            return Task.FromResult(_batches
                .Select(batch => new BatchNumber
                {
                    ItemCode = Item,
                    BatchNum = batch.Batch,
                    Quantity = batch.Quantity,
                    ExpiryDate = batch.Expiry,
                    AdmissionDate = batch.Admission
                })
                .ToList());
        }
    }

    // ---------------------------------------------------------------

    private static CreateInvoiceRequest OneLineInvoice() => new()
    {
        CardCode = "C-1",
        DocCurrency = "USD",
        Lines =
        [
            new()
            {
                ItemCode = Item,
                Quantity = 12,
                UnitPrice = 5m,
                WarehouseCode = Warehouse
            }
        ]
    };

    /// <summary>A SAP client whose stock read fails, and whose item read works.</summary>
    private static ISAPServiceLayerClient StockReadThrows(Exception? failure = null) =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            // Not batch- or serial-managed, so the line goes down the plain stock path.
            nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => Task.FromResult<Item?>(new Item
            {
                ItemCode = Item,
                ManageBatchNumbers = "tNO",
                ManageSerialNumbers = "tNO"
            }),
            nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) =>
                Task.FromException<List<StockQuantityDto>>(
                    failure ?? new TimeoutException("SAP stock read exceeded its 60-second budget.")),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

    private static ISAPServiceLayerClient StockReadReturns(decimal inStock, decimal committed) =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => Task.FromResult<Item?>(new Item
            {
                ItemCode = Item,
                ManageBatchNumbers = "tNO",
                ManageSerialNumbers = "tNO"
            }),
            nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) =>
                Task.FromResult<List<StockQuantityDto>>(
                [
                    new()
                    {
                        ItemCode = Item,
                        ItemName = "Cheddar 1kg",
                        WarehouseCode = Warehouse,
                        InStock = inStock,
                        Committed = committed
                    }
                ]),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

    /// <summary>A batch-managed item whose warehouse batch read fails.</summary>
    private static ISAPServiceLayerClient BatchManagedItemWhoseBatchReadThrows() =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => Task.FromResult<Item?>(new Item
            {
                ItemCode = Item,
                ManageBatchNumbers = "tYES",
                ManageSerialNumbers = "tNO"
            }),
            nameof(ISAPServiceLayerClient.GetBatchNumbersForItemInWarehouseAsync) =>
                Task.FromException<List<BatchNumber>>(
                    new TimeoutException("SAP batch read exceeded its 60-second budget.")),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

    /// <summary>A batch-managed item whose warehouse holds these batches, earliest expiry first.</summary>
    private static ISAPServiceLayerClient BatchManagedItemHolding(params (string Batch, decimal Quantity)[] batches) =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => Task.FromResult<Item?>(new Item
            {
                ItemCode = Item,
                ManageBatchNumbers = "tYES",
                ManageSerialNumbers = "tNO"
            }),
            nameof(ISAPServiceLayerClient.GetBatchNumbersForItemInWarehouseAsync) =>
                Task.FromResult(batches
                    .Select((batch, index) => new BatchNumber
                    {
                        ItemCode = Item,
                        BatchNum = batch.Batch,
                        Quantity = batch.Quantity,
                        ExpiryDate = new DateTime(2026, 10, 1).AddDays(index).ToString("yyyy-MM-dd")
                    })
                    .ToList()),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

    private static BatchInventoryValidationService CreateService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        SAPSettings? settings = null,
        ILogger<BatchInventoryValidationService>? logger = null) =>
        new(
            context,
            sapClient,
            StubProxy.Unused<IInventoryLockService>(),
            Options.Create(settings ?? new SAPSettings()),
            logger ?? new CapturingLogger<BatchInventoryValidationService>());

    private static ApplicationDbContext InMemoryContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
        context.Database.EnsureCreated();
        return context;
    }
}
