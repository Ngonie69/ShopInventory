using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Commands.CreateInventoryTransfer;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the difference between "SAP says there is not enough" and "SAP did not answer".
/// </summary>
/// <remarks>
/// Conflating the two cost a day of inventory transfers on 2026-09-02. A slow Service Layer made
/// the warehouse read fail; the validator answered by re-asking once per line, each of those
/// reported 0 on failure, and the document came back carrying a shortage nobody had. On the
/// submission path the re-asks ran until the browser's own five-minute timeout fired and took the
/// submission with them — eleven of thirteen attempts that day.
/// </remarks>
public sealed class InventoryTransferUnreadStockTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public InventoryTransferUnreadStockTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
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

    // ── The validator ───────────────────────────────────

    [Fact]
    public async Task A_source_warehouse_SAP_cannot_answer_for_is_unread_not_short()
    {
        var sap = new StockReadStub { StockReadThrows = true };

        var result = await Validate(sap, ItemLine("ITEM-A", 5));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.False(result.StockWasFullyRead);
        Assert.Equal(["WH-1"], result.UnreadableWarehouses);

        // The point of the fix: one failed warehouse read, not one more failed read per line.
        Assert.Equal(1, sap.StockReads);
    }

    [Fact]
    public async Task A_batch_read_SAP_cannot_answer_for_is_unread_not_short()
    {
        var sap = new StockReadStub { BatchReadThrows = true };

        var result = await Validate(sap, BatchLine("ITEM-A", 5, "B-A"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.False(result.StockWasFullyRead);
        Assert.Equal(["WH-1"], result.UnreadableWarehouses);
        Assert.Equal(1, sap.BatchReads);
    }

    [Fact]
    public async Task An_unread_warehouse_does_not_hide_a_shortage_in_one_that_answered()
    {
        var sap = new StockReadStub { StockReadThrowsForWarehouse = "WH-2" };

        var readable = ItemLine("ITEM-A", 500);
        var unread = ItemLine("ITEM-B", 1);
        unread.FromWarehouseCode = "WH-2";

        var result = await Validate(sap, readable, unread);

        // WH-1 answered and is genuinely short; WH-2 never answered and is reported as unread.
        Assert.False(result.IsValid);
        Assert.Equal("ITEM-A", Assert.Single(result.Errors).ItemCode);
        Assert.Equal(["WH-2"], result.UnreadableWarehouses);
    }

    [Fact]
    public async Task A_warehouse_that_answers_still_reports_a_real_shortage()
    {
        var sap = new StockReadStub();

        var result = await Validate(sap, ItemLine("ITEM-A", 500));

        Assert.False(result.IsValid);
        Assert.True(result.StockWasFullyRead);
        var error = Assert.Single(result.Errors);
        Assert.Equal("ITEM-A", error.ItemCode);
        Assert.Equal(10, error.AvailableQuantity);
    }

    [Fact]
    public async Task A_warehouse_that_answers_with_enough_stock_passes()
    {
        var sap = new StockReadStub();

        var result = await Validate(sap, ItemLine("ITEM-A", 2));

        Assert.True(result.IsValid);
        Assert.True(result.StockWasFullyRead);
    }

    // ── The submission ──────────────────────────────────

    [Fact]
    public async Task A_submission_is_held_when_stock_validation_outruns_its_budget()
    {
        var user = await AddSubmitterAsync();

        var result = await Handler(BudgetedValidation(NeverAnswers)).Handle(
            new CreateInventoryTransferCommand(Request(), user.Id), default);

        Assert.False(result.IsError);
        Assert.True(result.Value.RequiresApproval);
        Assert.Single(_context.PendingInventoryTransfers);
    }

    [Fact]
    public async Task A_submission_is_held_when_SAP_never_answered_for_the_warehouse()
    {
        var user = await AddSubmitterAsync();
        var unread = new StockValidationResult();
        unread.UnreadableWarehouses.Add("WH-1");

        var result = await Handler(BudgetedValidation(_ => Task.FromResult(unread))).Handle(
            new CreateInventoryTransferCommand(Request(), user.Id), default);

        Assert.False(result.IsError);
        Assert.True(result.Value.RequiresApproval);
        Assert.Single(_context.PendingInventoryTransfers);
    }

    [Fact]
    public async Task A_submission_SAP_positively_rejects_is_still_refused()
    {
        var user = await AddSubmitterAsync();
        var shortage = StockValidationResult.Failure([
            new StockValidationError
            {
                LineNumber = 1,
                ItemCode = "ITEM-A",
                WarehouseCode = "WH-1",
                RequestedQuantity = 5,
                AvailableQuantity = 0
            }
        ]);

        var result = await Handler(BudgetedValidation(_ => Task.FromResult(shortage))).Handle(
            new CreateInventoryTransferCommand(Request(), user.Id), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.InsufficientStock", result.FirstError.Code);
        Assert.Empty(_context.PendingInventoryTransfers);
    }

    [Fact]
    public async Task A_client_that_really_hangs_up_still_fails_the_submission()
    {
        var user = await AddSubmitterAsync();
        using var clientGoesAway = new CancellationTokenSource();

        // The abort lands while stock validation is in flight, which is how a browser giving up
        // actually reaches the handler. The budget must not swallow it into a silent hold.
        var result = await Handler(BudgetedValidation(async token =>
        {
            await clientGoesAway.CancelAsync();
            return await NeverAnswers(token);
        })).Handle(new CreateInventoryTransferCommand(Request(), user.Id), clientGoesAway.Token);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.CreationFailed", result.FirstError.Code);
        Assert.Empty(_context.PendingInventoryTransfers);
    }

    // ── The post ────────────────────────────────────────

    [Fact]
    public void An_unread_warehouse_is_not_worded_as_a_permanent_stock_rejection()
    {
        // The poster and the queue drain both refuse an unmeasured document. Neither may word it
        // the way a shortage is worded: SapFailureClassifier retires an entry that reads that way,
        // and this is the failure that should be retried once SAP answers again.
        const string posterMessage =
            "Could not read stock from SAP for warehouse(s) WH-1. " +
            "The transfer has not been posted; retry it once SAP is answering.";
        const string queueMessage =
            "Could not read stock from SAP for warehouse(s) WH-1; the transfer was not posted.";

        Assert.False(SapFailureClassifier.IsPermanentStockRejection(posterMessage));
        Assert.False(SapFailureClassifier.IsPermanentStockRejection(queueMessage));

        // Negative control: a real shortage still reads as one.
        Assert.True(SapFailureClassifier.IsPermanentStockRejection(
            "Insufficient stock in source warehouse: ITEM-A requested 5, available 0"));
    }

    // ── Harness ─────────────────────────────────────────

    private Task<StockValidationResult> Validate(
        StockReadStub sap,
        params CreateInventoryTransferLineRequest[] lines) =>
        new StockValidationService(_context, sap.AsClient(), NullLogger<StockValidationService>.Instance)
            .ValidateInventoryTransferStockAsync(new CreateInventoryTransferRequest
            {
                FromWarehouse = "WH-1",
                ToWarehouse = "WH-9",
                Lines = [.. lines]
            });

    private static CreateInventoryTransferLineRequest ItemLine(string itemCode, decimal quantity) => new()
    {
        ItemCode = itemCode,
        Quantity = quantity,
        UoMCode = "EA",
        FromWarehouseCode = "WH-1",
        ToWarehouseCode = "WH-9"
    };

    private static CreateInventoryTransferLineRequest BatchLine(
        string itemCode,
        decimal quantity,
        string batchNumber)
    {
        var line = ItemLine(itemCode, quantity);
        line.BatchNumbers = [new TransferBatchRequest { BatchNumber = batchNumber, Quantity = quantity }];
        return line;
    }

    /// <summary>A validation that never returns until its budget cancels it.</summary>
    private static Task<StockValidationResult> NeverAnswers(CancellationToken token) =>
        Task.Delay(Timeout.Infinite, token).ContinueWith(
            _ => new StockValidationResult(),
            TaskContinuationOptions.OnlyOnRanToCompletion);

    private static IStockValidationService BudgetedValidation(
        Func<CancellationToken, Task<StockValidationResult>> validate) =>
        StubProxy.For<IStockValidationService>((method, args) => method.Name switch
        {
            nameof(IStockValidationService.ValidateInventoryTransferStockAsync) =>
                (object)validate((CancellationToken)args![1]!),
            _ => throw new InvalidOperationException($"Unexpected stock-validation call: {method.Name}")
        });

    private CreateInventoryTransferHandler Handler(IStockValidationService stockValidation) =>
        new(
            _context,
            StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetWarehousesAsync) =>
                    (object)Task.FromResult(new List<WarehouseDto>
                    {
                        new() { WarehouseCode = "WH-1", WarehouseName = "WH-1", IsActive = true },
                        new() { WarehouseCode = "WH-9", WarehouseName = "WH-9", IsActive = true }
                    }),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            }),
            stockValidation,
            new InventoryTransferApprovalService(
                _context,
                new NoOpNotificationService(),
                NullLogger<InventoryTransferApprovalService>.Instance),
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            StubProxy.Unused<IIdempotencyRequestStore>(),
            Options.Create(new SAPSettings
            {
                Enabled = true,
                // Short enough to keep the test quick; the production default is 45s.
                TransferStockValidationBudgetSeconds = 1
            }),
            NullLogger<CreateInventoryTransferHandler>.Instance);

    private static CreateInventoryTransferRequest Request() => new()
    {
        FromWarehouse = "WH-1",
        ToWarehouse = "WH-9",
        Lines = [ItemLine("ITEM-A", 1)]
    };

    private async Task<User> AddSubmitterAsync()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"submitter-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.DepotController,
            IsActive = true
        };
        user.SetWarehouseCodes(["WH-1", "WH-9"]);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// A Service Layer that answers for a warehouse and can be told to stop answering, standing in
    /// for the slow-then-silent behaviour seen in production.
    /// </summary>
    private sealed class StockReadStub
    {
        public bool StockReadThrows { get; init; }
        public bool BatchReadThrows { get; init; }
        public string? StockReadThrowsForWarehouse { get; init; }
        public int StockReads { get; private set; }
        public int BatchReads { get; private set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) =>
                    ReadStock((string)args![0]!),
                nameof(ISAPServiceLayerClient.GetBatchNumbersForItemsInWarehouseAsync) =>
                    ReadBatches((string)args![1]!),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<List<StockQuantityDto>> ReadStock(string warehouse)
        {
            StockReads++;
            if (StockReadThrows
                || string.Equals(warehouse, StockReadThrowsForWarehouse, StringComparison.OrdinalIgnoreCase))
            {
                throw new TimeoutException($"SAP stock read exceeded its budget ({warehouse}).");
            }

            return Task.FromResult(new List<StockQuantityDto>
            {
                new() { ItemCode = "ITEM-A", WarehouseCode = warehouse, InStock = 10 },
                new() { ItemCode = "ITEM-B", WarehouseCode = warehouse, InStock = 10 }
            });
        }

        private Task<List<BatchNumber>> ReadBatches(string warehouse)
        {
            BatchReads++;
            if (BatchReadThrows)
            {
                throw new TimeoutException($"SAP batch read exceeded its budget ({warehouse}).");
            }

            return Task.FromResult(new List<BatchNumber>
            {
                new() { ItemCode = "ITEM-A", BatchNum = "B-A", Quantity = 10, Warehouse = warehouse }
            });
        }
    }
}
