using ErrorOr;
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
using ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A transfer out of a warehouse a till sells from must leave behind what the till has sold and SAP
/// has not yet invoiced.
/// </summary>
/// <remarks>
/// The case these are built on, from production on 2026-09-28: vending sale
/// KEF-FAC-20260928-C35E53C1E13E sold 8 YOG145 at KEFGRC at 13:28 CAT, checked against SAP's 685.
/// At 13:31 transfer 89450 converted VAN002's stock request and took all 685, and the sale — already
/// on a ZIMRA receipt — was refused by SAP seven times for stock that had left on the van.
/// </remarks>
public sealed class TransfersLeaveUnpostedTillStockTests : IDisposable
{
    private const string Depot = "KEFGRC";
    private const string Van = "VAN002";
    private const string Item = "YOG145";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;

    public TransfersLeaveUnpostedTillStockTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── The validator ───────────────────────────────────

    [Fact]
    public async Task A_transfer_of_everything_SAP_shows_is_refused_when_a_till_has_sold_some_of_it()
    {
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Pending);

        var result = await Validate(Line(Item, 685));

        var error = Assert.Single(result.Errors);
        Assert.Equal(Item, error.ItemCode);
        Assert.Equal(685, error.RequestedQuantity);
        Assert.Equal(677, error.AvailableQuantity);
        Assert.Equal(8, error.HeldForUnpostedSales);
        Assert.StartsWith("Insufficient stock", error.Message);
        Assert.Contains("SAP holds 685, but 8 of them are already sold at the till", error.Message);

        // Still read as a stock refusal, so the pending-transfer and queue classifiers treat it as
        // they always have.
        Assert.True(SapFailureClassifier.IsPermanentStockRejection(error.Message));
    }

    [Fact]
    public async Task What_is_left_after_the_till_sales_may_still_move()
    {
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Pending);

        var result = await Validate(Line(Item, 677));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task A_till_sale_already_in_SAP_holds_nothing_back()
    {
        // Consolidated means SAP has invoiced it: its units are already out of SAP's 685.
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Consolidated);

        var result = await Validate(Line(Item, 685));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task A_till_sale_in_another_warehouse_holds_nothing_back_here()
    {
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Pending, warehouse: "KEFSHOP");

        var result = await Validate(Line(Item, 685));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Lines_of_the_same_item_are_measured_together()
    {
        // Each fits beside the sale on its own; together they would take its units.
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Pending);

        var result = await Validate(Line(Item, 400), Line(Item, 285));

        var error = Assert.Single(result.Errors);
        Assert.Equal(1, error.LineNumber);
        Assert.Equal(685, error.RequestedQuantity);
        Assert.Equal(677, error.AvailableQuantity);
    }

    [Fact]
    public async Task A_line_already_short_against_SAP_is_not_reported_twice()
    {
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Pending);

        var result = await Validate(Line(Item, 700));

        var error = Assert.Single(result.Errors);
        Assert.Equal(685, error.AvailableQuantity);
        Assert.Equal(0, error.HeldForUnpostedSales);
    }

    [Fact]
    public async Task Other_items_on_the_transfer_are_unaffected()
    {
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Pending);

        var result = await Validate(Line(Item, 677), Line("YOG143", 1475));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task A_warehouse_SAP_did_not_answer_for_is_left_unread_not_netted()
    {
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Pending);
        var sap = new DepotSap { StockReadThrows = true };

        var result = await Validate(sap, Line(Item, 685));

        Assert.Empty(result.Errors);
        Assert.False(result.StockWasFullyRead);
    }

    // ── Converting a van's stock request ────────────────

    [Fact]
    public async Task Converting_a_stock_request_that_would_take_sold_units_is_refused_and_nothing_posts()
    {
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Pending);
        var sap = new DepotSap();

        var result = await ConvertHandler(sap).Handle(new ConvertTransferRequestCommand(7001, Guid.NewGuid()), default);

        Assert.True(result.IsError);
        Assert.Equal("InventoryTransfer.InsufficientStock", result.FirstError.Code);
        Assert.Contains("SAP holds 685, but 8 of them are already sold at the till", result.FirstError.Description);
        Assert.Equal(0, sap.Conversions);
    }

    [Fact]
    public async Task Converting_a_stock_request_that_leaves_the_sold_units_behind_posts()
    {
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Pending);
        var sap = new DepotSap { RequestedQuantity = 677 };

        var result = await ConvertHandler(sap).Handle(new ConvertTransferRequestCommand(7001, Guid.NewGuid()), default);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(1, sap.Conversions);
    }

    [Fact]
    public async Task Converting_from_a_warehouse_with_no_unposted_till_sales_reads_no_stock()
    {
        // Most conversions: a depot no till sells from. It must cost exactly what it did before.
        var sap = new DepotSap();

        var result = await ConvertHandler(sap).Handle(new ConvertTransferRequestCommand(7001, Guid.NewGuid()), default);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(1, sap.Conversions);
        Assert.Equal(0, sap.StockReads);
    }

    [Fact]
    public async Task Converting_while_SAP_stock_cannot_be_read_is_refused_not_posted_blind()
    {
        await SeedTillSaleAsync(Item, 8, DesktopSaleConsolidationStatus.Pending);
        var sap = new DepotSap { StockReadThrows = true };

        var result = await ConvertHandler(sap).Handle(new ConvertTransferRequestCommand(7001, Guid.NewGuid()), default);

        Assert.True(result.IsError);
        Assert.Contains("try again in a minute", result.FirstError.Description);
        Assert.Equal(0, sap.Conversions);
    }

    // ── Harness ─────────────────────────────────────────

    private Task<StockValidationResult> Validate(params CreateInventoryTransferLineRequest[] lines) =>
        Validate(new DepotSap(), lines);

    private Task<StockValidationResult> Validate(DepotSap sap, params CreateInventoryTransferLineRequest[] lines) =>
        Validation(sap).ValidateInventoryTransferStockAsync(new CreateInventoryTransferRequest
        {
            FromWarehouse = Depot,
            ToWarehouse = Van,
            Lines = [.. lines]
        });

    private StockValidationService Validation(DepotSap sap) =>
        new(_context, sap.AsClient(), TillClaims(), NullLogger<StockValidationService>.Instance);

    private UnpostedTillClaims TillClaims() =>
        new(_context, Options.Create(new DailyStockSettings()), NullLogger<UnpostedTillClaims>.Instance);

    private static CreateInventoryTransferLineRequest Line(string itemCode, decimal quantity) => new()
    {
        ItemCode = itemCode,
        Quantity = quantity,
        FromWarehouseCode = Depot,
        ToWarehouseCode = Van
    };

    private ConvertTransferRequestHandler ConvertHandler(DepotSap sap) =>
        new(
            sap.AsClient(),
            new InventoryTransferApprovalService(
                _context,
                new NoOpNotificationService(),
                NullLogger<InventoryTransferApprovalService>.Instance),
            StubProxy.For<ITransferWarehouseAuthorizer>((method, _) => method.Name switch
            {
                nameof(ITransferWarehouseAuthorizer.EnsureCanConvertRequestAsync) =>
                    Task.FromResult<ErrorOr<Success>>(Result.Success),
                // Null scope: an admin or stock controller, who converts directly.
                nameof(ITransferWarehouseAuthorizer.GetSourceScopeAsync) =>
                    Task.FromResult<IReadOnlyList<string>?>(null),
                _ => throw new InvalidOperationException($"Unexpected authorizer call: {method.Name}")
            }),
            new IdempotencyRequestStore(new SingleDbContextScopeFactory(_options), Options.Create(new SecuritySettings())),
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            new NoOpNotificationService(),
            Validation(sap),
            TillClaims(),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<ConvertTransferRequestHandler>.Instance);

    private async Task SeedTillSaleAsync(
        string itemCode,
        decimal quantity,
        DesktopSaleConsolidationStatus status,
        string warehouse = Depot)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = $"KEF-FAC-{Guid.NewGuid():N}",
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = "COR006",
            WarehouseCode = warehouse,
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
            WarehouseCode = warehouse,
            Quantity = quantity
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>
    /// KEFGRC as SAP showed it at 13:31: 685 YOG145 and plenty of YOG143, and VAN002's request for
    /// all 685.
    /// </summary>
    private sealed class DepotSap
    {
        public bool StockReadThrows { get; init; }
        public decimal RequestedQuantity { get; init; } = 685;
        public int StockReads { get; private set; }
        public int Conversions { get; private set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetStockQuantitiesForItemsInWarehouseAsync) =>
                    ReadStock((string)args![0]!),
                nameof(ISAPServiceLayerClient.GetInventoryTransferRequestByDocEntryAsync) =>
                    Task.FromResult<InventoryTransferRequest?>(Request((int)args![0]!)),
                nameof(ISAPServiceLayerClient.ConvertTransferRequestToTransferAsync) =>
                    Convert(),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private InventoryTransferRequest Request(int docEntry) => new()
        {
            DocEntry = docEntry,
            DocNum = 89450,
            FromWarehouse = Depot,
            ToWarehouse = Van,
            StockTransferLines =
            [
                new InventoryTransferRequestLine
                {
                    LineNum = 0,
                    ItemCode = Item,
                    Quantity = RequestedQuantity,
                    FromWarehouseCode = Depot,
                    WarehouseCode = Van
                }
            ]
        };

        private Task<InventoryTransfer> Convert()
        {
            Conversions++;
            return Task.FromResult(new InventoryTransfer
            {
                DocEntry = 90001,
                DocNum = 89450,
                FromWarehouse = Depot,
                ToWarehouse = Van
            });
        }

        private Task<List<StockQuantityDto>> ReadStock(string warehouse)
        {
            StockReads++;
            if (StockReadThrows)
            {
                throw new TimeoutException($"SAP stock read exceeded its budget ({warehouse}).");
            }

            return Task.FromResult(new List<StockQuantityDto>
            {
                new() { ItemCode = Item, WarehouseCode = warehouse, InStock = 685 },
                new() { ItemCode = "YOG143", WarehouseCode = warehouse, InStock = 2305 }
            });
        }
    }
}
