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
