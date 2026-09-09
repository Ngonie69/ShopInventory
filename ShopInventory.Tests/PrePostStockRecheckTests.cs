using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The stock re-read that happens under the inventory locks, immediately before an invoice is
/// posted to SAP.
/// </summary>
/// <remarks>
/// <c>PrePostValidationAsync</c> is documented as the last check before posting: take the locks,
/// read stock again, refuse if it moved. It did not do that. Whenever the caller passed an
/// allocation — which is every well-formed invoice — it returned "Stock validation successful"
/// straight after acquiring the locks, having read nothing. The only stock read that ever informed a
/// web invoice was the one taken in the pass <i>before</i> the locks existed, and the gap between
/// that read and the POST is exactly where a concurrent consumer takes the stock.
///
/// <para>
/// The second test here is the one that would have caught it: stock is sufficient when the first
/// pass looks and gone when the second does. Under the old code the invoice was cleared to post.
/// </para>
/// </remarks>
public sealed class PrePostStockRecheckTests
{
    private const string Item = "CHE011";
    private const string Warehouse = "KEFSHOP";

    [Fact]
    public async Task Stock_is_read_again_after_the_locks_are_held()
    {
        await using var context = InMemoryContext();
        var sap = new CountingStockClient(_ => Stock(inStock: 50));
        var service = CreateService(context, sap);

        var firstPass = await service.ValidateAndAllocateBatchesAsync(Invoice(quantity: 12));
        var readsAfterFirstPass = sap.StockReads;

        await service.PrePostValidationAsync(Invoice(quantity: 12), firstPass.AllocatedLines);

        Assert.True(
            sap.StockReads > readsAfterFirstPass,
            $"Pre-post validation read no stock at all: {readsAfterFirstPass} reads before, {sap.StockReads} after.");
    }

    [Fact]
    public async Task An_invoice_is_refused_when_the_stock_goes_between_the_two_passes()
    {
        await using var context = InMemoryContext();

        // Enough on the first look, taken by someone else before the second.
        var sap = new CountingStockClient(readNumber => Stock(inStock: readNumber == 1 ? 50 : 4));
        var service = CreateService(context, sap);

        var firstPass = await service.ValidateAndAllocateBatchesAsync(Invoice(quantity: 12));
        Assert.True(firstPass.IsValid);

        var prePost = await service.PrePostValidationAsync(Invoice(quantity: 12), firstPass.AllocatedLines);

        Assert.False(prePost.IsValid);
        Assert.Contains(prePost.Errors, e => e.ErrorCode == BatchValidationErrorCode.InsufficientTotalStock);
    }

    [Fact]
    public async Task The_locks_are_released_when_the_re_read_refuses()
    {
        await using var context = InMemoryContext();
        var locks = new RecordingLockService();
        var sap = new CountingStockClient(readNumber => Stock(inStock: readNumber == 1 ? 50 : 4));
        var service = CreateService(context, sap, locks);

        var firstPass = await service.ValidateAndAllocateBatchesAsync(Invoice(quantity: 12));
        var prePost = await service.PrePostValidationAsync(Invoice(quantity: 12), firstPass.AllocatedLines);

        Assert.False(prePost.IsValid);

        // Held locks on a refused invoice would block the next attempt at the same items for the
        // whole lock lifetime, turning one refusal into a stall.
        Assert.Equal(locks.Acquired, locks.Released);
        Assert.NotEmpty(locks.Released);
    }

    [Fact]
    public async Task An_invoice_still_posts_when_the_stock_holds()
    {
        await using var context = InMemoryContext();
        var sap = new CountingStockClient(_ => Stock(inStock: 50));
        var service = CreateService(context, sap);

        var firstPass = await service.ValidateAndAllocateBatchesAsync(Invoice(quantity: 12));
        var prePost = await service.PrePostValidationAsync(Invoice(quantity: 12), firstPass.AllocatedLines);

        Assert.True(prePost.IsValid);
        Assert.Empty(prePost.Errors);
    }

    [Fact]
    public async Task Stock_that_merely_moved_but_is_still_enough_is_a_warning_not_a_refusal()
    {
        await using var context = InMemoryContext();

        // 50 down to 20, against a line needing 12. Somebody else bought some; there is still
        // plenty. Refusing here would reject a correct invoice every time a warehouse was busy.
        var sap = new CountingStockClient(readNumber => Stock(inStock: readNumber == 1 ? 50 : 20));
        var service = CreateService(context, sap);

        var firstPass = await service.ValidateAndAllocateBatchesAsync(Invoice(quantity: 12));
        var prePost = await service.PrePostValidationAsync(Invoice(quantity: 12), firstPass.AllocatedLines);

        Assert.True(prePost.IsValid);
    }

    // ---------------------------------------------------------------
    // The cost of the extra pass
    // ---------------------------------------------------------------

    [Fact]
    public async Task One_pass_asks_SAP_for_the_same_item_and_warehouse_once()
    {
        await using var context = InMemoryContext();
        var sap = new CountingStockClient(_ => Stock(inStock: 50));
        var service = CreateService(context, sap);

        // Two lines of the same item in the same warehouse. Before the pass memo this cost three
        // reads — one per line, plus the aggregate check — and the whole point of the memo is that
        // adding a second pass did not increase what an invoice costs SAP. There are only six
        // process-wide slots and a stock read is what fills them.
        await service.ValidateAndAllocateBatchesAsync(TwoLineInvoice());

        Assert.Equal(1, sap.StockReads);
    }

    [Fact]
    public async Task The_second_pass_does_not_reuse_the_first_pass_reading()
    {
        await using var context = InMemoryContext();
        var sap = new CountingStockClient(_ => Stock(inStock: 50));
        var service = CreateService(context, sap);

        await service.ValidateAndAllocateBatchesAsync(Invoice(quantity: 12));
        Assert.Equal(1, sap.StockReads);

        await service.ValidateAndAllocateBatchesAsync(Invoice(quantity: 12));

        // A memo that survived between passes would put back the hole it was written to close.
        Assert.Equal(2, sap.StockReads);
    }

    [Fact]
    public async Task An_unreadable_warehouse_is_asked_about_once_per_pass_not_once_per_line()
    {
        await using var context = InMemoryContext();
        var sap = new CountingStockClient(_ => throw new TimeoutException("SAP stock read exceeded its budget."));
        var service = CreateService(context, sap);

        var result = await service.ValidateAndAllocateBatchesAsync(TwoLineInvoice());

        Assert.False(result.IsValid);

        // During an outage every one of these holds a slot until it times out. A twenty-line
        // document should not spend twenty of them reaching the verdict the first one gave.
        Assert.Equal(1, sap.StockReads);
    }

    // ---------------------------------------------------------------

    private static CreateInvoiceRequest Invoice(decimal quantity) => new()
    {
        CardCode = "C-1",
        DocCurrency = "USD",
        Lines = [Line(quantity)]
    };

    private static CreateInvoiceRequest TwoLineInvoice() => new()
    {
        CardCode = "C-1",
        DocCurrency = "USD",
        Lines = [Line(6), Line(6)]
    };

    private static CreateInvoiceLineRequest Line(decimal quantity) => new()
    {
        ItemCode = Item,
        Quantity = quantity,
        UnitPrice = 5m,
        WarehouseCode = Warehouse
    };

    private static List<StockQuantityDto> Stock(decimal inStock) =>
    [
        new()
        {
            ItemCode = Item,
            ItemName = "Cheddar 1kg",
            WarehouseCode = Warehouse,
            InStock = inStock,
            Committed = 0
        }
    ];

    /// <summary>
    /// A SAP client that counts stock reads and can answer each one differently, so a test can put
    /// stock on the shelf for one pass and take it away before the next.
    /// </summary>
    private sealed class CountingStockClient
    {
        private readonly Func<int, List<StockQuantityDto>> _answer;

        public CountingStockClient(Func<int, List<StockQuantityDto>> answer) => _answer = answer;

        public int StockReads { get; private set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => Task.FromResult<Item?>(new Item
                {
                    ItemCode = Item,
                    ManageBatchNumbers = "tNO",
                    ManageSerialNumbers = "tNO"
                }),
                nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) => Read(),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<List<StockQuantityDto>> Read()
        {
            StockReads++;
            try
            {
                return Task.FromResult(_answer(StockReads));
            }
            catch (Exception ex)
            {
                return Task.FromException<List<StockQuantityDto>>(ex);
            }
        }
    }

    /// <summary>Records what was locked and what was let go, so a leak is visible.</summary>
    private sealed class RecordingLockService
    {
        public List<string> Acquired { get; } = [];
        public List<string> Released { get; } = [];

        public IInventoryLockService AsService() =>
            StubProxy.For<IInventoryLockService>((method, args) => method.Name switch
            {
                nameof(IInventoryLockService.TryAcquireMultipleLocksAsync) => AcquireAll(args),
                nameof(IInventoryLockService.ReleaseMultipleLocksAsync) => ReleaseAll(args),
                _ => throw new InvalidOperationException($"Unexpected lock call: {method.Name}")
            });

        private Task<MultiLockResult> AcquireAll(object?[]? args)
        {
            var requests = (List<InventoryLockRequest>)args![0]!;
            var tokens = requests.Select(request => $"lock:{request.ToLockKey()}").ToList();
            Acquired.AddRange(tokens);

            return Task.FromResult(new MultiLockResult
            {
                AllAcquired = true,
                LockTokens = tokens,
                CombinedLockToken = string.Join(",", tokens),
                EarliestExpiry = DateTime.UtcNow.AddSeconds(30)
            });
        }

        private Task ReleaseAll(object?[]? args)
        {
            Released.AddRange((List<string>)args![0]!);
            return Task.CompletedTask;
        }
    }

    private static BatchInventoryValidationService CreateService(
        ApplicationDbContext context,
        CountingStockClient sap,
        RecordingLockService? locks = null) =>
        new(
            context,
            sap.AsClient(),
            locks?.AsService() ?? new RecordingLockService().AsService(),
            Options.Create(new SAPSettings()),
            NullLogger<BatchInventoryValidationService>.Instance);

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
