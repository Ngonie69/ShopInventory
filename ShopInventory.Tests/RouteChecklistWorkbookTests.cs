using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The route checklist workbook, opened and read back: every shop on the chosen routes is marked
/// ORDERED or NO ORDER, and what an ordered shop asked for sits under it.
/// </summary>
public sealed class RouteChecklistWorkbookTests
{
    private readonly ReportExportService _service = new();
    private static readonly DateTime Day = new(2026, 10, 28);

    private static SalesOrderDto Order(int id, string cardCode, string cardName, string currency, params (string Item, decimal Qty, decimal Total)[] lines) => new()
    {
        Id = id,
        OrderNumber = $"SO-{id}",
        CardCode = cardCode,
        CardName = cardName,
        Status = SalesOrderStatus.Pending,
        Currency = currency,
        OrderDate = Day.AddHours(10),
        DocTotal = lines.Sum(line => line.Total),
        CreatedByUserName = "merch1",
        Lines = lines.Select((line, index) => new SalesOrderLineDto
        {
            LineNum = index,
            ItemCode = line.Item,
            ItemDescription = line.Item + " description",
            Quantity = line.Qty,
            UnitPrice = line.Total / line.Qty,
            LineTotal = line.Total,
            UoMCode = "EA"
        }).ToList()
    };

    private byte[] Export(IEnumerable<SalesOrderDto> orders, params string[] routes)
    {
        var checklist = RouteOrderChecklist.Build(DeliveryRouteDirectory.Build([]), orders);
        var chosen = checklist.Routes.Where(route => routes.Contains(route.Route.Name)).ToList();
        return _service.ExportRouteChecklistToExcel(new RouteChecklistExport(
            Day, false, "All routes", null, chosen, checklist.OffRouteOrders, checklist.SkippedOrderCount,
            order => order.Status.ToString()));
    }

    private static IXLRow RowWhere(IXLWorksheet sheet, int column, string value) =>
        sheet.RowsUsed().First(row => row.Cell(column).GetString() == value);

    [Fact]
    public void Each_shop_is_marked_and_an_ordered_shop_carries_its_lines()
    {
        var bridge = Order(1, "SPA070", "Spar Bridge Yellowcob Ent P/L t/a ZiG", "ZWG", ("MILK1L", 12, 120m), ("YOG500", 6, 48m));

        using var workbook = new XLWorkbook(new MemoryStream(Export([bridge], "BORROWDALE"))) ;
        Assert.Equal(["Checklist", "Route Summary", "Order Lines", "Not Ordered"], workbook.Worksheets.Select(sheet => sheet.Name));

        var sheet = workbook.Worksheet("Checklist");
        var shopRow = RowWhere(sheet, 1, "Spar Bridge Yellowcob Ent P/L t/a");
        Assert.Equal("ORDERED", shopRow.Cell(3).GetString());
        Assert.Equal("SO-1", shopRow.Cell(4).GetString());
        Assert.Equal("MILK1L", shopRow.Cell(7).GetString());
        Assert.Equal(12m, shopRow.Cell(9).GetValue<decimal>());
        Assert.Equal("YOG500", sheet.Row(shopRow.RowNumber() + 1).Cell(7).GetString());
        Assert.Equal(168m, shopRow.Cell(13).GetValue<decimal>());
        Assert.NotEqual(sheet.Cell(shopRow.RowNumber(), 3).Style.Fill.BackgroundColor,
            RowWhere(sheet, 1, "Eskbank").Cell(3).Style.Fill.BackgroundColor);

        var eskbank = RowWhere(sheet, 1, "Eskbank");
        Assert.Equal("NO ORDER", eskbank.Cell(3).GetString());
        Assert.True(eskbank.Cell(4).IsEmpty());

        var banner = sheet.CellsUsed().Single(cell => cell.GetString().StartsWith("BORROWDALE", StringComparison.Ordinal));
        Assert.Contains("1 of", banner.GetString());
    }

    [Fact]
    public void Not_ordered_lists_the_shops_still_waiting_and_none_that_ordered()
    {
        var bridge = Order(1, "SPA050 USD", "Spar Bridge Yellowcob Ent P/L t/a", "USD", ("MILK1L", 1, 10m));

        using var workbook = new XLWorkbook(new MemoryStream(Export([bridge], "BORROWDALE")));
        var shops = workbook.Worksheet("Not Ordered").Column(3).CellsUsed().Select(cell => cell.GetString()).ToList();

        Assert.Contains("Eskbank", shops);
        Assert.DoesNotContain("Spar Bridge Yellowcob Ent P/L t/a", shops);
    }

    [Fact]
    public void Order_lines_lists_an_order_once_and_totals_each_currency_apart()
    {
        var usd = Order(1, "SPA050 USD", "Spar Bridge Yellowcob Ent P/L t/a", "USD", ("MILK1L", 2, 20m), ("YOG500", 1, 5m));
        var zig = Order(2, "SPA070", "Spar Bridge Yellowcob Ent P/L t/a ZiG", "ZWG", ("MILK1L", 1, 300m));
        var offRoute = Order(3, "TMP114", "TM Lobengula", "USD", ("MILK1L", 1, 10m));

        using var workbook = new XLWorkbook(new MemoryStream(Export([usd, zig, offRoute], "BORROWDALE", "WEST 2")));
        var sheet = workbook.Worksheet("Order Lines");

        var orderNumbers = sheet.Column(4).CellsUsed().Select(cell => cell.GetString()).Where(text => text.StartsWith("SO-")).ToList();
        Assert.Equal(["SO-1", "SO-1", "SO-2", "SO-3"], orderNumbers);
        Assert.Equal("(no route)", RowWhere(sheet, 4, "SO-3").Cell(1).GetString());

        var usdTotal = sheet.RowsUsed().Single(row => row.Cell(15).GetString() == "USD" && row.Cell(13).GetString().StartsWith("Total"));
        Assert.Equal(35m, usdTotal.Cell(14).GetValue<decimal>());
        var zigTotal = sheet.RowsUsed().Single(row => row.Cell(15).GetString() == "ZWG" && row.Cell(13).GetString().StartsWith("Total"));
        Assert.Equal(300m, zigTotal.Cell(14).GetValue<decimal>());
    }

    [Fact]
    public void A_shop_on_no_route_is_shown_under_its_own_banner()
    {
        var offRoute = Order(3, "TMP114", "TM Lobengula", "USD", ("MILK1L", 1, 10m));

        using var workbook = new XLWorkbook(new MemoryStream(Export([offRoute], "BORROWDALE")));
        var sheet = workbook.Worksheet("Checklist");

        Assert.Contains(sheet.CellsUsed(), cell => cell.GetString().StartsWith("SHOPS ON NO ROUTE", StringComparison.Ordinal));
        Assert.Equal("ORDERED", RowWhere(sheet, 1, "TM Lobengula").Cell(3).GetString());
    }

    [Fact]
    public void The_route_summary_counts_shops_and_values_by_currency()
    {
        var usd = Order(1, "SPA050 USD", "Spar Bridge Yellowcob Ent P/L t/a", "USD", ("MILK1L", 2, 20m));

        using var workbook = new XLWorkbook(new MemoryStream(Export([usd], "BORROWDALE")));
        var sheet = workbook.Worksheet("Route Summary");
        var row = RowWhere(sheet, 1, "BORROWDALE");

        Assert.Equal(1, row.Cell(5).GetValue<int>());
        Assert.Equal(row.Cell(4).GetValue<int>() - 1, row.Cell(6).GetValue<int>());
        Assert.Equal(20m, row.Cell(9).GetValue<decimal>());
        Assert.Equal("Value USD", RowWhere(sheet, 1, "Route").Cell(9).GetString());
    }
}
