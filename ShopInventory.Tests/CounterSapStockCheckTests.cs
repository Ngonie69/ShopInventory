using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Sales;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A till sale is checked against SAP's own batch stock before it is taken and fiscalised.
/// </summary>
/// <remarks>
/// The first test is the sale that made this exist. KEF-FAC-20260915-8F904ECE7543 at KEFSHOP sold 22 of
/// an item. The ledger said there were enough; SAP held 21 in batches. The sale was fiscalised in the
/// request, and only the posting job, afterwards, found SAP short — six times, with the ZIMRA receipt
/// already printed. The ledger is deliberately generous in these tests: every refusal here is SAP's.
/// </remarks>
public sealed class CounterSapStockCheckTests : IDisposable
{
    private const string Shop = "KEFSHOP";
    private const string Cheese = "ICA004";
    private const string Container = "CON020";

    private static readonly Guid TillUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private readonly DailyStockSettings _settings = new()
    {
        MonitoredWarehouses = [Shop],
        ReconcileWarehouses = [Shop]
    };

    /// <summary>SAP's batches of <see cref="Cheese"/> at the shop.</summary>
    private readonly List<(string Batch, decimal Quantity)> _sapBatches = [];

    private Func<CancellationToken, Task<List<BatchNumber>>>? _batchRead;
    private Exception? _itemReadFailure;
    private int _batchReads;

    public CounterSapStockCheckTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
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

    // ---------------------------------------------------------------
    // The check
    // ---------------------------------------------------------------

    [Fact]
    public async Task The_sale_SAP_could_not_supply_is_refused_before_it_is_taken()
    {
        _sapBatches.Add(("B1", 21m));

        var result = await CheckAsync(Line(Cheese, 22m));

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SapStockShort", result.FirstError.Code);
        Assert.Contains("ICA004: 22 on this sale, only 21 left in SAP at KEFSHOP", result.FirstError.Description);
        Assert.Contains("nothing was sold", result.FirstError.Description);
    }

    /// <summary>
    /// A Validation error answers 400. The till shows the server's reason only for a 400 or 422 — a 409
    /// reaches the cashier as "it may or may not have been created", which is exactly wrong here.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_a_400_so_the_till_shows_the_cashier_why()
    {
        _sapBatches.Add(("B1", 21m));

        var short_ = await CheckAsync(Line(Cheese, 22m));
        _batchRead = _ => Task.FromException<List<BatchNumber>>(new HttpRequestException("SAP is down"));
        var unreadable = await CheckAsync(Line(Cheese, 1m));

        Assert.Equal(ErrorType.Validation, short_.FirstError.Type);
        Assert.Equal(ErrorType.Validation, unreadable.FirstError.Type);
    }

    [Fact]
    public async Task A_sale_SAP_can_supply_passes()
    {
        _sapBatches.Add(("B1", 15m));
        _sapBatches.Add(("B2", 10m));

        var result = await CheckAsync(Line(Cheese, 22m));

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Two_lines_of_the_same_item_are_one_demand_on_SAP()
    {
        _sapBatches.Add(("B1", 21m));

        var result = await CheckAsync(Line(Cheese, 10m), Line(Cheese, 12m));

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SapStockShort", result.FirstError.Code);
    }

    /// <summary>
    /// Five sold a minute ago and not posted yet. SAP still shows them, and that sale's post will take
    /// them before this one's does, so this sale can have 16, not 21.
    /// </summary>
    [Fact]
    public async Task Stock_already_sold_and_not_yet_in_SAP_is_set_aside()
    {
        _sapBatches.Add(("B1", 21m));
        await SeedSaleAsync(Cheese, 5m, DesktopSaleConsolidationStatus.Pending);

        var refused = await CheckAsync(Line(Cheese, 17m));
        var allowed = await CheckAsync(Line(Cheese, 16m));

        Assert.True(refused.IsError);
        Assert.Contains("once the 5 already sold but not yet in SAP are taken off", refused.FirstError.Description);
        Assert.False(allowed.IsError);
    }

    /// <summary>A refused sale still holds its units until someone resolves it — the one in the incident did.</summary>
    [Fact]
    public async Task A_sale_SAP_keeps_refusing_is_still_set_aside()
    {
        _sapBatches.Add(("B1", 21m));
        await SeedSaleAsync(Cheese, 22m, DesktopSaleConsolidationStatus.Failed);

        var result = await CheckAsync(Line(Cheese, 1m));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_sale_SAP_already_has_is_not_counted_twice()
    {
        _sapBatches.Add(("B1", 21m));
        await SeedSaleAsync(Cheese, 5m, DesktopSaleConsolidationStatus.Consolidated);

        var result = await CheckAsync(Line(Cheese, 21m));

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task A_failed_SAP_read_refuses_the_sale()
    {
        _batchRead = _ => Task.FromException<List<BatchNumber>>(new HttpRequestException("SAP is down"));

        var result = await CheckAsync(Line(Cheese, 1m));

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SapStockUnreadable", result.FirstError.Code);
        Assert.Contains("nothing was sold", result.FirstError.Description);
    }

    [Fact]
    public async Task An_item_SAP_cannot_describe_refuses_the_sale()
    {
        _itemReadFailure = new HttpRequestException("SAP is down");

        var result = await CheckAsync(Line(Cheese, 1m));

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SapStockUnreadable", result.FirstError.Code);
    }

    /// <summary>
    /// The SQLQueries stock reads are known to hang for minutes. A cashier cannot wait for that, and the
    /// sale holds inventory locks while it waits.
    /// </summary>
    [Fact]
    public async Task A_read_that_does_not_answer_in_time_refuses_the_sale()
    {
        _settings.CounterSapCheckSeconds = 1;
        _batchRead = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return [];
        };

        var started = DateTime.UtcNow;
        var result = await CheckAsync(Line(Cheese, 1m));

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SapStockUnreadable", result.FirstError.Code);
        Assert.Contains("did not answer within 1 seconds", result.FirstError.Description);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    /// <summary>The same as the post: SAP needs no selection for a non-batch line, so it is not read.</summary>
    [Fact]
    public async Task A_non_batch_item_is_not_read_from_SAP()
    {
        _batchRead = _ => throw new InvalidOperationException("A non-batch line must not read batches.");

        var result = await CheckAsync(Line(Container, 50m));

        Assert.False(result.IsError);
        Assert.Equal(0, _batchReads);
    }

    [Fact]
    public async Task With_the_check_switched_off_SAP_is_not_asked()
    {
        _settings.CheckSapStockAtCounter = false;
        _itemReadFailure = new InvalidOperationException("SAP must not be asked.");

        var result = await CheckAsync(Line(Cheese, 22m));

        Assert.False(result.IsError);
    }

    /// <summary>
    /// The posting job's wording is read by SapFailureClassifier, so a post, which passes no claims,
    /// must say exactly what it always said.
    /// </summary>
    [Fact]
    public async Task The_posting_message_is_unchanged_when_nothing_is_set_aside()
    {
        _sapBatches.Add(("B1", 21m));

        var result = await Allocator().ValidateAndAllocateBatchesAsync(
            CounterSapStockCheck.BuildRequest([Line(Cheese, 22m)], Shop),
            checkNonBatchStock: false);

        Assert.Equal(
            "Insufficient remaining batch stock for line 1. Need 22.0000, available 21.0000 after other invoice lines",
            Assert.Single(result.ValidationErrors).Message);
    }

    // ---------------------------------------------------------------
    // Through the real handler
    // ---------------------------------------------------------------

    /// <summary>
    /// The incident, end to end: the ledger has 30 and would allow it, SAP has 21. No sale row is
    /// written, nothing is taken off the ledger, and nothing reaches the fiscaliser — the handler is
    /// built without one, so a sale that got that far would throw instead of refusing.
    /// </summary>
    [Fact]
    public async Task The_handler_refuses_before_the_ledger_or_the_receipt()
    {
        await SeedLedgerAsync(Cheese, 30m);
        _sapBatches.Add(("B1", 21m));

        var result = await SellAsync(fiscalize: true, Line(Cheese, 22m));

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SapStockShort", result.FirstError.Code);
        Assert.Equal(0, await _context.DesktopSales.CountAsync());
        Assert.Equal(30m, await LedgerAsync(Cheese));
    }

    [Fact]
    public async Task The_handler_still_sells_what_SAP_can_supply()
    {
        await SeedLedgerAsync(Cheese, 30m);
        _sapBatches.Add(("B1", 21m));

        var result = await SellAsync(fiscalize: false, Line(Cheese, 21m));

        Assert.False(result.IsError);
        Assert.Equal(1, await _context.DesktopSales.CountAsync());
        Assert.Equal(9m, await LedgerAsync(Cheese));
    }

    /// <summary>
    /// The ledger's own refusal, with SAP holding plenty. It was a 409, which the till shows as "may or
    /// may not have been created" and never as a stock refusal.
    /// </summary>
    [Fact]
    public async Task The_ledger_refusal_is_a_400_too()
    {
        await SeedLedgerAsync(Cheese, 5m);
        _sapBatches.Add(("B1", 50m));

        var result = await SellAsync(fiscalize: true, Line(Cheese, 6m));

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.StockLedgerRefused", result.FirstError.Code);
        Assert.Equal(ErrorType.Validation, result.FirstError.Type);
        Assert.Contains("ICA004 in KEFSHOP: 6 requested, 5 left to promise today", result.FirstError.Description);
        Assert.Equal(0, await _context.DesktopSales.CountAsync());
    }

    /// <summary>
    /// What reaches the till on the wire: a 400 whose <c>errors</c> dictionary carries the reason, which
    /// is the entry the till puts in front of the cashier.
    /// </summary>
    [Theory]
    [InlineData("DesktopSales.StockLedgerRefused")]
    [InlineData("DesktopSales.SapStockShort")]
    [InlineData("DesktopSales.SapStockUnreadable")]
    public void Every_stock_refusal_answers_400_with_its_reason(string code)
    {
        const string reason = "ICA004 in KEFSHOP: 22 requested, 21 left";
        var error = code switch
        {
            "DesktopSales.StockLedgerRefused" => ShopInventory.Common.Errors.Errors.DesktopSales.StockLedgerRefused(reason),
            "DesktopSales.SapStockShort" => ShopInventory.Common.Errors.Errors.DesktopSales.SapStockShort(reason),
            _ => ShopInventory.Common.Errors.Errors.DesktopSales.SapStockUnreadable(reason)
        };

        var controller = new RefusingController
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    Request = { Path = "/api/DesktopIntegration/sales" }
                }
            }
        };

        var response = Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.ObjectResult>(controller.Refuse([error]));
        var body = Assert.IsType<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>(response.Value);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(reason, Assert.Single(body.Errors[code]));
    }

    private sealed class RefusingController : ShopInventory.Controllers.ApiControllerBase
    {
        public Microsoft.AspNetCore.Mvc.IActionResult Refuse(List<Error> errors) => Problem(errors);
    }

    [Fact]
    public async Task The_handler_refuses_when_SAP_cannot_be_read()
    {
        await SeedLedgerAsync(Cheese, 30m);
        _batchRead = _ => Task.FromException<List<BatchNumber>>(new HttpRequestException("SAP is down"));

        var result = await SellAsync(fiscalize: true, Line(Cheese, 1m));

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SapStockUnreadable", result.FirstError.Code);
        Assert.Equal(0, await _context.DesktopSales.CountAsync());
        Assert.Equal(30m, await LedgerAsync(Cheese));
    }

    // ---------------------------------------------------------------

    private static CreateDesktopSaleLineRequest Line(string itemCode, decimal quantity) => new()
    {
        ItemCode = itemCode,
        Quantity = quantity,
        UnitPrice = 8.15m,
        WarehouseCode = Shop
    };

    private async Task<ErrorOr<Success>> CheckAsync(params CreateDesktopSaleLineRequest[] lines)
    {
        var result = await Check().CheckAsync(lines, Shop, "KEF-FAC-TEST", CancellationToken.None);
        _context.ChangeTracker.Clear();
        return result;
    }

    private CounterSapStockCheck Check() => new(
        _context,
        Allocator(),
        Ledger(),
        Options.Create(_settings),
        NullLogger<CounterSapStockCheck>.Instance);

    private StockLedger Ledger() => new(_context, Options.Create(_settings), NullLogger<StockLedger>.Instance);

    private BatchInventoryValidationService Allocator() => new(
        _context,
        SapClient(),
        StubProxy.Unused<IInventoryLockService>(),
        Options.Create(new SAPSettings()),
        NullLogger<BatchInventoryValidationService>.Instance);

    private ISAPServiceLayerClient SapClient() =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetItemByCodeAsync) => _itemReadFailure is not null
                ? Task.FromException<Item?>(_itemReadFailure)
                : Task.FromResult<Item?>(new Item
                {
                    ItemCode = (string)args![0]!,
                    ManageBatchNumbers = (string)args[0]! == Container ? "tNO" : "tYES",
                    ManageSerialNumbers = "tNO"
                }),

            nameof(ISAPServiceLayerClient.GetBatchNumbersForItemInWarehouseAsync) =>
                ReadBatches((CancellationToken)args![2]!),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

    private Task<List<BatchNumber>> ReadBatches(CancellationToken token)
    {
        _batchReads++;

        return _batchRead?.Invoke(token) ?? Task.FromResult(_sapBatches
            .Select((batch, index) => new BatchNumber
            {
                ItemCode = Cheese,
                BatchNum = batch.Batch,
                Quantity = batch.Quantity,
                Warehouse = Shop,
                ExpiryDate = new DateTime(2026, 10, 1).AddDays(index).ToString("yyyy-MM-dd")
            })
            .ToList());
    }

    private async Task<ErrorOr<CreateDesktopSaleResult>> SellAsync(
        bool fiscalize,
        params CreateDesktopSaleLineRequest[] lines)
    {
        if (!await _context.Users.AnyAsync(user => user.Id == TillUserId))
        {
            _context.Users.Add(new User
            {
                Id = TillUserId,
                Username = "kefshop-till",
                PasswordHash = "not-used-here",
                Role = "Cashier",
                IsActive = true,
                AssignedBusinessPartnerCode = "CIS006",
                AssignedWarehouseCodes = JsonSerializer.Serialize(new[] { Shop })
            });
            await _context.SaveChangesAsync();
        }

        var handler = new CreateDesktopSaleHandler(
            _context,
            // Never reached by a refused sale, and a sale that is not refused is sent with
            // Fiscalize = false. Anything that reached it would throw.
            null!,
            Locks(),
            Ledger(),
            Check(),
            Hub(),
            StubProxy.Unused<IIdempotencyRequestStore>(),
            Options.Create(new TaxSettings()),
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            NullLogger<CreateDesktopSaleHandler>.Instance);

        var request = new CreateDesktopSaleRequest
        {
            SourceSystem = SaleSourceSystems.ShopTill,
            Fiscalize = fiscalize,
            PaymentMethod = TenderTypes.Cash,
            DocCurrency = "USD",
            Lines = lines.ToList()
        };

        var result = await handler.Handle(new CreateDesktopSaleCommand(request, TillUserId), CancellationToken.None);
        _context.ChangeTracker.Clear();
        return result;
    }

    private static IInventoryLockService Locks() =>
        StubProxy.For<IInventoryLockService>((method, _) => method.Name switch
        {
            nameof(IInventoryLockService.TryAcquireMultipleLocksAsync) =>
                Task.FromResult(new MultiLockResult { AllAcquired = true, LockTokens = ["token"] }),
            nameof(IInventoryLockService.ReleaseMultipleLocksAsync) => Task.CompletedTask,
            _ => throw new InvalidOperationException($"Unexpected lock call: {method.Name}")
        });

    private static IHubContext<NotificationHub> Hub()
    {
        var client = StubProxy.For<IClientProxy>((_, _) => Task.CompletedTask);
        var clients = StubProxy.For<IHubClients>((_, _) => client);
        return StubProxy.For<IHubContext<NotificationHub>>((_, _) => clients);
    }

    private async Task SeedSaleAsync(string itemCode, decimal quantity, DesktopSaleConsolidationStatus status)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = $"TILL-{Guid.NewGuid():N}",
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = "CIS006",
            WarehouseCode = Shop,
            ConsolidationStatus = status,
            CreatedAt = DateTime.UtcNow,
            DocDate = DateTime.UtcNow.Date
        };

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();

        _context.DesktopSaleLines.Add(new DesktopSaleLineEntity
        {
            SaleId = sale.Id,
            LineNum = 1,
            ItemCode = itemCode,
            WarehouseCode = Shop,
            Quantity = quantity
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task SeedLedgerAsync(string itemCode, decimal quantity)
    {
        var snapshot = new DailyStockSnapshotEntity
        {
            SnapshotDate = StockLedgerDay.Today(_settings.StockFetchTimeCAT),
            WarehouseCode = Shop,
            Status = StockSnapshotStatus.Complete
        };
        _context.DailyStockSnapshots.Add(snapshot);
        await _context.SaveChangesAsync();

        _context.DailyStockSnapshotItems.Add(new DailyStockSnapshotItemEntity
        {
            SnapshotId = snapshot.Id,
            ItemCode = itemCode,
            WarehouseCode = Shop,
            BatchNumber = "B1",
            // See SnapshotSqliteContext for why the concurrency token is set by hand here.
            Version = 1
        }.Opened(quantity));

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<decimal> LedgerAsync(string itemCode) =>
        await _context.DailyStockSnapshotItems
            .AsNoTracking()
            .Where(row => row.WarehouseCode == Shop && row.ItemCode == itemCode)
            .SumAsync(row => row.AvailableQuantity);
}
