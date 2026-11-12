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

    /// <summary>
    /// The page's codes and its batches must come from the same read: a code from a newer list than its
    /// batches reaches the handset with none, and it falls back to the company-wide quantity for it.
    /// </summary>
    [Fact]
    public async Task The_page_is_paged_over_the_codes_of_the_batch_read_it_shows()
    {
        var pagedCodes = new List<IReadOnlyList<string>>();

        var page = await ReadPageAsync(vanSaleOnly: true, pagedCodes: pagedCodes);

        Assert.Equal(["YOG004", "YOG008", "YOG099"], Assert.Single(pagedCodes));
        Assert.DoesNotContain(page.Products!.SelectMany(product => product.Batches!), batch => batch.BatchNumber == "YOG099/J01/26");
    }

    [Fact]
    public async Task A_page_says_when_SAP_was_read_and_whether_that_read_is_standing_in()
    {
        var readAt = DateTimeOffset.UtcNow.AddMinutes(-25);

        var stale = await ReadPageAsync(vanSaleOnly: true, readAt: readAt, stale: true);
        var current = await ReadPageAsync(vanSaleOnly: true);

        Assert.True(stale.BatchesStale);
        Assert.Equal(readAt.UtcDateTime, stale.BatchesAsOfUtc);
        Assert.False(current.BatchesStale);
    }

    /// <summary>
    /// The safety half of answering from an older read. A sale SAP took after the read is not in its figure,
    /// so it must still come off on the handset, or the van shows goods it has sold.
    /// </summary>
    [Fact]
    public async Task A_sale_SAP_took_after_the_read_still_comes_off_on_a_stale_page()
    {
        await UploadPostedSaleAsync("YOG008", 4m, postedAt: DateTime.UtcNow.AddMinutes(-20));

        var stale = await ReadPageAsync(vanSaleOnly: true, readAt: DateTimeOffset.UtcNow.AddMinutes(-30), stale: true);

        Assert.Equal(4m, Product(stale, "YOG008").QuantityAwaitingSap);
    }

    [Fact]
    public async Task A_sale_SAP_took_before_the_read_does_not_come_off_twice()
    {
        await UploadPostedSaleAsync("YOG008", 4m, postedAt: DateTime.UtcNow.AddMinutes(-20));

        var current = await ReadPageAsync(vanSaleOnly: true, readAt: DateTimeOffset.UtcNow.AddMinutes(-10));

        Assert.Equal(0m, Product(current, "YOG008").QuantityAwaitingSap);
    }

    private static ProductDto Product(WarehouseProductsPagedResponseDto page, string code) =>
        page.Products!.Single(product => product.ItemCode == code);

    private async Task<WarehouseProductsPagedResponseDto> ReadPageAsync(
        bool vanSaleOnly,
        DateTimeOffset? readAt = null,
        bool stale = false,
        List<IReadOnlyList<string>>? pagedCodes = null)
    {
        var handler = new GetPagedProductsInWarehouseHandler(
            VanStockInSap(readAt ?? DateTimeOffset.UtcNow, stale, pagedCodes ?? []),
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
    /// <param name="readAt">When the batch read the page is built from was sent.</param>
    /// <param name="stale">Whether that read is standing in while SAP is asked again.</param>
    /// <param name="pagedCodes">Collects the codes the handler asks to page over.</param>
    private static ISAPServiceLayerClient VanStockInSap(
        DateTimeOffset readAt,
        bool stale,
        List<IReadOnlyList<string>> pagedCodes)
    {
        var items = new List<Item>
        {
            new() { ItemCode = "YOG004", ItemName = "1Kg Banana Smooth Yoghurt", ManageBatchNumbers = "tYES", QuantityOnStock = 5m },
            new() { ItemCode = "YOG008", ItemName = "1Kg Double Cream Yoghurt", ManageBatchNumbers = "tYES", QuantityOnStock = 11m }
        };

        var batches = new List<BatchNumber>
        {
            new() { ItemCode = "YOG004", BatchNum = "YOG004/J22/26", Quantity = 5m, Warehouse = Van },
            new() { ItemCode = "YOG008", BatchNum = "YOG008/J25/26", Quantity = 11m, Warehouse = Van },
            // Another item on the van that is not on this page: its batches must not leak onto it.
            new() { ItemCode = "YOG099", BatchNum = "YOG099/J01/26", Quantity = 3m, Warehouse = Van }
        };

        var snapshot = new WarehouseBatchSnapshot(batches, readAt, stale, ["YOG004", "YOG008", "YOG099"]);

        return StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetWarehouseBatchesForDisplayAsync) =>
                Task.FromResult(snapshot),
            nameof(ISAPServiceLayerClient.GetItemPageFromCodesAsync) =>
                Record(pagedCodes, (IReadOnlyList<string>)args![0]!,
                    Task.FromResult(new WarehouseItemPage(items, HasMore: false, NextCursor: null))),
            _ => null
        });
    }

    private static T Record<T>(List<IReadOnlyList<string>> into, IReadOnlyList<string> codes, T result)
    {
        into.Add(codes);
        return result;
    }

    private async Task UploadPostedSaleAsync(string item, decimal quantity, DateTime postedAt)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = "VAN004-INV-20261112-0A1B2C",
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "VAN008",
            DocDate = DateTime.UtcNow.Date,
            TotalAmount = 19.56m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            PostedAt = postedAt,
            WarehouseCode = Van,
            CreatedAt = postedAt.AddMinutes(-5),
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
