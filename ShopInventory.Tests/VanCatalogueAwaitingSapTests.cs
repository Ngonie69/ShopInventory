using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Products.Queries.GetPagedProductsInWarehouse;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The van catalogue read carries, beside SAP's figure, what of the van's sales SAP has yet to take.
/// </summary>
/// <remarks>
/// The handset reads <c>quantityAwaitingSap</c> being present at all as "this server knows", and only then
/// stops taking its own day's sales off SAP's figure. So the field must be there on every van page —
/// zero, not absent, for an item with nothing waiting — and must not appear on the web's read of the
/// same route, which has no use for it.
/// </remarks>
public sealed class VanCatalogueAwaitingSapTests : IDisposable
{
    private const string Van = "VAN004";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanCatalogueAwaitingSapTests()
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
    public async Task A_van_page_carries_what_SAP_has_yet_to_take_and_zero_for_the_rest()
    {
        await UploadUnpostedSaleAsync("YOG008", 5m);

        var page = await ReadPageAsync(vanSaleOnly: true);

        Assert.Equal(5m, Product(page, "YOG008").QuantityAwaitingSap);
        Assert.Equal(0m, Product(page, "YOG004").QuantityAwaitingSap);

        // SAP's own figures are passed through untouched; the handset does the subtraction.
        Assert.Equal(11m, Product(page, "YOG008").QuantityInStock);
    }

    [Fact]
    public async Task The_webs_read_of_the_same_route_does_not_carry_it()
    {
        await UploadUnpostedSaleAsync("YOG008", 5m);

        var page = await ReadPageAsync(vanSaleOnly: false);

        Assert.All(page.Products!, product => Assert.Null(product.QuantityAwaitingSap));
    }

    private static ProductDto Product(WarehouseProductsPagedResponseDto page, string code) =>
        page.Products!.Single(product => product.ItemCode == code);

    private async Task<WarehouseProductsPagedResponseDto> ReadPageAsync(bool vanSaleOnly)
    {
        var handler = new GetPagedProductsInWarehouseHandler(
            VanStockInSap(),
            StubProxy.Unused<ILocalPriceCatalogService>(),
            _context,
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(new DailyStockSettings()),
            NullLogger<GetPagedProductsInWarehouseHandler>.Instance);

        var result = await handler.Handle(
            new GetPagedProductsInWarehouseQuery(Van, PageSize: 50, VanSaleOnly: vanSaleOnly, UseCursor: true),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    /// <summary>SAP after transfer 89562: 11 of YOG008 and 5 of YOG004 on VAN004, one batch each.</summary>
    private static ISAPServiceLayerClient VanStockInSap()
    {
        var items = new List<Item>
        {
            new() { ItemCode = "YOG004", ItemName = "1Kg Banana Smooth Yoghurt", ManageBatchNumbers = "tYES", QuantityOnStock = 5m },
            new() { ItemCode = "YOG008", ItemName = "1Kg Double Cream Yoghurt", ManageBatchNumbers = "tYES", QuantityOnStock = 11m }
        };

        var batches = new List<BatchNumber>
        {
            new() { ItemCode = "YOG004", BatchNum = "YOG004/J22/26", Quantity = 5m, Warehouse = Van },
            new() { ItemCode = "YOG008", BatchNum = "YOG008/J25/26", Quantity = 11m, Warehouse = Van }
        };

        return StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetItemPageInWarehouseAsync) =>
                Task.FromResult(new WarehouseItemPage(items, HasMore: false, NextCursor: null)),
            nameof(ISAPServiceLayerClient.GetBatchNumbersForItemsInWarehouseAsync) =>
                Task.FromResult(batches),
            _ => null
        });
    }

    private async Task UploadUnpostedSaleAsync(string item, decimal quantity)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = "VAN004-INV-20260929-5839DB",
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "VAN008",
            DocDate = DateTime.UtcNow.Date,
            TotalAmount = 24.45m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            WarehouseCode = Van,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = item,
                    Quantity = quantity,
                    UnitPrice = 4.89m,
                    LineTotal = quantity * 4.89m,
                    WarehouseCode = Van
                }
            ]
        });

        await _context.SaveChangesAsync();
    }
}
