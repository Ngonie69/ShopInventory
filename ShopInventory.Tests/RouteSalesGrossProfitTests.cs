using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerProductMix;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerProductSales;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSales;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSalesSummary;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins SAP's booked gross profit on the route reports — the route summary, its product mix and product
/// sales, and one customer's sales — each costed by its own posted sales' invoices, offline and online,
/// and a sale not yet in SAP left out of the profit but not out of the takings.
/// </summary>
public sealed class RouteSalesGrossProfitTests : IDisposable
{
    private const string Route = "VAN010";
    private static readonly DateTime Day = new(2026, 9, 1);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly FixedCosts _costs = new();
    private readonly int _tuckShopId;

    public RouteSalesGrossProfitTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        var tuck = new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = Route, Code = "TUCK01", Name = "Tuck Shop", IsActive = true,
            CreatedAt = Day.AddDays(-30)
        };
        _context.RouteCustomers.Add(tuck);
        _context.SaveChanges();
        _tuckShopId = tuck.Id;

        // Offline: posted as 501 (costed). Online: posted as 601 (costed). Offline: not yet in SAP.
        _context.DesktopSales.Add(Offline("OFF-1", 501, [("CHE011", 2, 20m), ("YOG023", 10, 5m)]));
        _context.DesktopSales.Add(Offline("OFF-2", null, [("CHE011", 1, 10m)]));
        _context.StockReservations.Add(Online("ON-1", 601, "CHE011", 3, 30m));
        _context.SaveChanges();

        _costs.Lines.Add(new SaleInvoiceLineCost(501, "CHE011", Route, 2m, 20m, 6m));
        _costs.Lines.Add(new SaleInvoiceLineCost(501, "YOG023", Route, 10m, 5m, -1m));
        _costs.Lines.Add(new SaleInvoiceLineCost(601, "CHE011", Route, 3m, 30m, 9m));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task The_route_summary_costs_each_customer_and_the_route()
    {
        var result = await new GetRouteCustomerSalesSummaryHandler(_context, _costs)
            .Handle(new GetRouteCustomerSalesSummaryQuery(Route, Day, Day, null, false), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.True(result.Value.Margin!.Available);

        var route = result.Value.Routes.Single();
        var total = route.TotalsByCurrency.Single();
        Assert.Equal((14m, 55m, 2, 3), (total.GrossProfit, total.CostedNetAmount, total.CostedSaleCount, total.SaleCount));
        Assert.Equal(25.5m, total.MarginPercent);
        Assert.Equal(14m, route.Customers.Single(customer => customer.Code == "TUCK01").TotalsByCurrency.Single().GrossProfit);

        await WriteFixtureAsync("route-sales-summary", result.Value);
    }

    [Fact]
    public async Task The_product_mix_and_product_sales_cost_each_item()
    {
        var mix = await new GetRouteCustomerProductMixHandler(_context, _costs)
            .Handle(new GetRouteCustomerProductMixQuery(Route, null, Day, Day, 0), CancellationToken.None);
        Assert.False(mix.IsError, mix.IsError ? mix.FirstError.Description : null);

        var cheese = mix.Value.Items.Single(item => item.ItemCode == "CHE011").TotalsByCurrency.Single();
        Assert.Equal((15m, 50m, 2), (cheese.GrossProfit, cheese.CostedNetAmount, cheese.CostedSaleCount));
        Assert.Equal(-1m, mix.Value.Items.Single(item => item.ItemCode == "YOG023").TotalsByCurrency.Single().GrossProfit);
        await WriteFixtureAsync("route-product-mix", mix.Value);

        var sales = await new GetRouteCustomerProductSalesHandler(_context, _costs)
            .Handle(new GetRouteCustomerProductSalesQuery(Route, Day, Day), CancellationToken.None);
        Assert.False(sales.IsError, sales.IsError ? sales.FirstError.Description : null);

        var row = sales.Value.Rows.Single(r => r.ItemCode == "CHE011");
        Assert.Equal((15m, 30.0m, 2), (row.GrossProfit, row.MarginPercent, row.CostedLineCount));
        await WriteFixtureAsync("route-product-sales", sales.Value);
    }

    [Fact]
    public async Task One_customers_sales_cost_each_sale_the_totals_and_the_mix()
    {
        var result = await new GetRouteCustomerSalesHandler(_context, _costs)
            .Handle(new GetRouteCustomerSalesQuery(_tuckShopId, Day, Day), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var detail = result.Value;
        Assert.True(detail.Margin!.Available);

        Assert.Equal(5m, detail.Sales.Single(sale => sale.Reference == "OFF-1").GrossProfit);
        Assert.Equal(9m, detail.Sales.Single(sale => sale.Reference == "ON-1").GrossProfit);
        Assert.Null(detail.Sales.Single(sale => sale.Reference == "OFF-2").GrossProfit);
        Assert.Equal((14m, 2), (detail.TotalsByCurrency.Single().GrossProfit, detail.TotalsByCurrency.Single().CostedSaleCount));
        Assert.Equal(15m, detail.ProductMix.Single(item => item.ItemCode == "CHE011").TotalsByCurrency.Single().GrossProfit);

        await WriteFixtureAsync("route-customer-sales", detail);
    }

    /// <summary>
    /// The portal mirrors these records by hand, so a nullable that does not match reads as nothing. This
    /// carries one customer's sales through the portal's copy, into its workbook and the vendor page's
    /// month and items.
    /// </summary>
    [Fact]
    public async Task A_customers_profit_reaches_the_portal_the_workbook_and_the_vendor_page()
    {
        var api = (await new GetRouteCustomerSalesHandler(_context, _costs)
            .Handle(new GetRouteCustomerSalesQuery(_tuckShopId, Day, Day), CancellationToken.None)).Value;

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var read = JsonSerializer.Deserialize<ShopInventory.Web.Models.RouteCustomerSalesDetailModel>(JsonSerializer.Serialize(api, web), web)!;

        Assert.True(read.Margin!.Available);
        Assert.Equal((14m, 25.5m, 2), (read.TotalsByCurrency.Single().GrossProfit, read.TotalsByCurrency.Single().MarginPercent, read.TotalsByCurrency.Single().CostedSaleCount));
        var off1 = read.Sales.Single(sale => sale.Reference == "OFF-1");
        Assert.Equal((5m, 20.0m), (off1.GrossProfit, off1.MarginPercent));
        Assert.Equal(6m, off1.Lines.Single(line => line.ItemCode == "CHE011").GrossProfit);

        var window = ShopInventory.Web.Features.Vending.VendorSalesDigest.Summarise(read.Sales);
        Assert.Equal((14m, 2), (window.GrossProfit, window.CostedSaleCount));
        var mix = ShopInventory.Web.Features.Vending.VendorSalesDigest.ProductMix(read.Sales, "USD", 10);
        Assert.Equal(15m, mix.Single(item => item.ItemCode == "CHE011").GrossProfit);
        Assert.Equal(-1m, mix.Single(item => item.ItemCode == "YOG023").GrossProfit);

        using var workbook = new ClosedXML.Excel.XLWorkbook(new MemoryStream(
            new ShopInventory.Web.Services.ReportExportService().ExportRouteCustomerSalesToExcel(read, "Van 010")));
        var sheet = workbook.Worksheet("Sales");
        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Gross Profit");
        var column = sheet.Column(header.Address.ColumnNumber).CellsUsed()
            .Where(cell => cell.Address.RowNumber > header.Address.RowNumber).Select(cell => cell.GetString()).ToList();
        Assert.Contains("5", column);
        Assert.Contains("Not in SAP yet", column);
        Assert.Contains(workbook.Worksheet("Sales").CellsUsed(), cell => cell.GetString().StartsWith("USD 14.00 (25.5%"));
    }

    private static async Task WriteFixtureAsync(string name, object value)
    {
        // Opt-in, for driving the real page with this result: GP_FIXTURE_DIR names a folder to write it to.
        if (Environment.GetEnvironmentVariable("GP_FIXTURE_DIR") is { Length: > 0 } dir)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, name + ".json"),
                JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    private DesktopSaleEntity Offline(string reference, int? sapDocEntry, (string Code, int Quantity, decimal Net)[] lines) => new()
    {
        ExternalReferenceId = reference,
        SourceSystem = SaleSourceSystems.VanSales,
        CardCode = Route,
        CardName = "Van 010",
        RouteCustomerId = _tuckShopId,
        RouteCustomerCode = "TUCK01",
        RouteCustomerName = "Tuck Shop",
        WarehouseCode = Route,
        DocDate = Day,
        TotalAmount = lines.Sum(line => line.Net),
        AmountPaid = lines.Sum(line => line.Net),
        Currency = "USD",
        PaymentMethod = "Cash",
        CreatedBy = Guid.NewGuid().ToString(),
        CreatedAt = DateTime.SpecifyKind(Day.AddHours(8), DateTimeKind.Utc),
        SapDocEntry = sapDocEntry,
        Lines = lines.Select((line, index) => new DesktopSaleLineEntity
        {
            LineNum = index,
            ItemCode = line.Code,
            ItemDescription = line.Code,
            Quantity = line.Quantity,
            UnitPrice = line.Net / line.Quantity,
            LineTotal = line.Net,
            WarehouseCode = Route,
        }).ToList(),
    };

    private StockReservationEntity Online(string reference, int sapDocEntry, string item, int quantity, decimal net)
    {
        var at = DateTime.SpecifyKind(Day.AddHours(8), DateTimeKind.Utc);
        return new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = Route,
            RouteCustomerId = _tuckShopId,
            RouteCustomerCode = "TUCK01",
            RouteCustomerName = "Tuck Shop",
            TotalValue = net,
            Currency = "USD",
            PaymentMethod = "Cash",
            Status = ReservationStatus.Confirmed,
            CreatedAt = at,
            ExpiresAt = at.AddHours(1),
            ConfirmedAt = at,
            SAPDocEntry = sapDocEntry,
            CreatedBy = Guid.NewGuid().ToString(),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0, ItemCode = item, ItemDescription = item, OriginalQuantity = quantity,
                    ReservedQuantity = quantity, UoMCode = "EA", WarehouseCode = Route,
                    UnitPrice = net / quantity, LineTotal = net
                }
            ]
        };
    }

    private sealed class FixedCosts : ISaleInvoiceCostReader
    {
        public List<SaleInvoiceLineCost> Lines { get; } = [];

        public Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
            IReadOnlyCollection<string> warehouseCodes, IReadOnlyCollection<int> docEntries, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SaleInvoiceLineCost>>(Lines
                .Where(line => docEntries.Contains(line.DocEntry)
                               && warehouseCodes.Contains(line.WarehouseCode, StringComparer.OrdinalIgnoreCase))
                .ToList());
    }
}
