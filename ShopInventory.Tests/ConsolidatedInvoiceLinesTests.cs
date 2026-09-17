using ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// How a consolidated invoice's lines are cut when some of its sales were converted from sales orders.
/// </summary>
public sealed class ConsolidatedInvoiceLinesTests
{
    private const int OrderDocEntry = 40211;

    /// <summary>
    /// The regression guard. With no orders involved the lines must come out exactly as the merge that
    /// existed before — one line per item and price, quantities summed, nothing based on anything.
    /// </summary>
    [Fact]
    public void Sales_with_no_order_merge_as_they_always_did()
    {
        var first = Sale(Line("NRI049", 4m, 60m), Line("CHE011", 1m, 12m));
        var second = Sale(Line("NRI049", 2m, 60m), Line("NRI049", 1m, 55m));

        var lines = ConsolidatedInvoiceLines.Build([first, second], NoOrders());

        Assert.Collection(
            lines,
            line => AssertLine(line, "NRI049", 6m, 60m, baseLine: null),
            line => AssertLine(line, "CHE011", 1m, 12m, baseLine: null),
            line => AssertLine(line, "NRI049", 1m, 55m, baseLine: null));

        // The merge this replaced, as it stood in ConsolidateDailySalesHandler.
        var before = new[] { first, second }
            .SelectMany(s => s.Lines)
            .GroupBy(l => new { l.ItemCode, l.WarehouseCode, l.UnitPrice, l.TaxCode, l.DiscountPercent, l.CostCentreCode })
            .Select(g => new CreateInvoiceLineRequest
            {
                ItemCode = g.Key.ItemCode,
                Quantity = g.Sum(l => l.Quantity),
                UnitPrice = g.Key.UnitPrice,
                WarehouseCode = g.Key.WarehouseCode,
                TaxCode = g.Key.TaxCode,
                DiscountPercent = g.Key.DiscountPercent,
                CostCentreCode = g.Key.CostCentreCode,
                AutoAllocateBatches = true
            })
            .ToList();

        Assert.Equal(
            before.Select(Describe),
            lines.Select(Describe));
    }

    private static string Describe(CreateInvoiceLineRequest line) =>
        $"{line.ItemCode}|{line.Quantity}|{line.UnitPrice}|{line.WarehouseCode}|{line.TaxCode}|"
        + $"{line.DiscountPercent}|{line.CostCentreCode}|{line.AutoAllocateBatches}|{line.BaseType}|{line.BaseEntry}|{line.BaseLine}";

    [Fact]
    public void A_converted_sale_is_based_on_the_order_line_for_its_item()
    {
        var sale = Sale(Line("NRI049", 4m, 60m), Line("CHE011", 2m, 12m));
        var order = Order(OrderLine(0, "CHE011", open: 2m), OrderLine(1, "NRI049", open: 4m));

        var lines = ConsolidatedInvoiceLines.Build([sale], Orders((sale, order)));

        Assert.Collection(
            lines,
            line => AssertLine(line, "NRI049", 4m, 60m, baseLine: 1),
            line => AssertLine(line, "CHE011", 2m, 12m, baseLine: 0));
    }

    /// <summary>
    /// SAP refuses a line that asks for more than the order has open, and refuses the whole invoice
    /// with it. What was delivered beyond the order still has to be invoiced, so it goes on its own
    /// ordinary line.
    /// </summary>
    [Fact]
    public void Quantity_beyond_what_the_order_has_open_goes_on_an_ordinary_line()
    {
        var sale = Sale(Line("NRI049", 10m, 60m));
        var order = Order(OrderLine(0, "NRI049", open: 6m));

        var lines = ConsolidatedInvoiceLines.Build([sale], Orders((sale, order)));

        Assert.Collection(
            lines,
            line => AssertLine(line, "NRI049", 6m, 60m, baseLine: 0),
            line => AssertLine(line, "NRI049", 4m, 60m, baseLine: null));
    }

    /// <summary>
    /// Two conversions of one order in the same day. The second must see what the first used, or the
    /// two linked lines together would ask SAP for more than the order had.
    /// </summary>
    [Fact]
    public void Two_sales_against_one_order_share_its_open_quantity()
    {
        var first = Sale(Line("NRI049", 4m, 60m));
        var second = Sale(Line("NRI049", 4m, 60m));
        var order = Order(OrderLine(0, "NRI049", open: 5m));

        var lines = ConsolidatedInvoiceLines.Build([first, second], Orders((first, order), (second, order)));

        Assert.Collection(
            lines,
            line => AssertLine(line, "NRI049", 5m, 60m, baseLine: 0),
            line => AssertLine(line, "NRI049", 3m, 60m, baseLine: null));
    }

    [Fact]
    public void An_item_split_over_several_order_lines_fills_them_in_line_order()
    {
        var sale = Sale(Line("NRI049", 5m, 60m));
        var order = Order(OrderLine(3, "NRI049", open: 4m), OrderLine(1, "NRI049", open: 2m));

        var lines = ConsolidatedInvoiceLines.Build([sale], Orders((sale, order)));

        Assert.Collection(
            lines,
            line => AssertLine(line, "NRI049", 2m, 60m, baseLine: 1),
            line => AssertLine(line, "NRI049", 3m, 60m, baseLine: 3));
    }

    [Fact]
    public void Closed_order_lines_and_items_not_on_the_order_are_not_linked()
    {
        var sale = Sale(Line("NRI049", 2m, 60m), Line("CHE011", 1m, 12m));
        var order = Order(
            OrderLine(0, "NRI049", open: 2m, status: "bost_Close"),
            OrderLine(1, "SOM001", open: 9m));

        var lines = ConsolidatedInvoiceLines.Build([sale], Orders((sale, order)));

        Assert.All(lines, line => Assert.Null(line.BaseEntry));
    }

    /// <summary>A sale in the group with no order of its own is untouched by another sale's order.</summary>
    [Fact]
    public void Only_the_converted_sale_is_linked()
    {
        var converted = Sale(Line("NRI049", 2m, 60m));
        var walkIn = Sale(Line("NRI049", 3m, 60m));
        var order = Order(OrderLine(0, "NRI049", open: 10m));

        var lines = ConsolidatedInvoiceLines.Build([converted, walkIn], Orders((converted, order)));

        Assert.Collection(
            lines,
            line => AssertLine(line, "NRI049", 2m, 60m, baseLine: 0),
            line => AssertLine(line, "NRI049", 3m, 60m, baseLine: null));
    }

    [Fact]
    public void Unlink_strips_the_base_document_and_says_whether_there_was_one()
    {
        var sale = Sale(Line("NRI049", 2m, 60m));
        var lines = ConsolidatedInvoiceLines.Build([sale], Orders((sale, Order(OrderLine(0, "NRI049", open: 2m)))));

        Assert.True(ConsolidatedInvoiceLines.Unlink(lines));
        Assert.All(lines, line =>
        {
            Assert.Null(line.BaseType);
            Assert.Null(line.BaseEntry);
            Assert.Null(line.BaseLine);
        });

        Assert.False(ConsolidatedInvoiceLines.Unlink(lines));
    }

    private static void AssertLine(
        CreateInvoiceLineRequest line,
        string itemCode,
        decimal quantity,
        decimal unitPrice,
        int? baseLine)
    {
        Assert.Equal(itemCode, line.ItemCode);
        Assert.Equal(quantity, line.Quantity);
        Assert.Equal(unitPrice, line.UnitPrice);
        Assert.True(line.AutoAllocateBatches);
        Assert.Equal(baseLine, line.BaseLine);

        if (baseLine.HasValue)
        {
            Assert.Equal(17, line.BaseType);
            Assert.Equal(OrderDocEntry, line.BaseEntry);
        }
        else
        {
            Assert.Null(line.BaseType);
            Assert.Null(line.BaseEntry);
        }
    }

    private static DesktopSaleEntity Sale(params DesktopSaleLineEntity[] lines) =>
        new() { CardCode = "VAN014", WarehouseCode = "VAN14", Lines = [.. lines] };

    private static DesktopSaleLineEntity Line(string itemCode, decimal quantity, decimal unitPrice) =>
        new() { ItemCode = itemCode, Quantity = quantity, UnitPrice = unitPrice, WarehouseCode = "VAN14" };

    private static SAPSalesOrder Order(params SAPSalesOrderLine[] lines) =>
        new() { DocEntry = OrderDocEntry, CardCode = "VAN014", DocumentStatus = "bost_Open", DocumentLines = [.. lines] };

    private static SAPSalesOrderLine OrderLine(int lineNum, string itemCode, decimal open, string status = "bost_Open") =>
        new() { LineNum = lineNum, ItemCode = itemCode, RemainingOpenQuantity = open, LineStatus = status };

    private static Dictionary<DesktopSaleEntity, SAPSalesOrder> NoOrders() => new(ReferenceEqualityComparer.Instance);

    private static Dictionary<DesktopSaleEntity, SAPSalesOrder> Orders(
        params (DesktopSaleEntity Sale, SAPSalesOrder Order)[] pairs)
    {
        var orders = NoOrders();
        foreach (var (sale, order) in pairs)
        {
            orders[sale] = order;
        }

        return orders;
    }
}
