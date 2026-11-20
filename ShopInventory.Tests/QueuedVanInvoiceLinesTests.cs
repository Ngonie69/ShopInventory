using ShopInventory.Features.DesktopIntegration.Commands.PostQueuedVanInvoices;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// A van sale linked to its customer's sales order must reach SAP at the total its receipt declared.
/// </summary>
/// <remarks>
/// <para>
/// The cases are ten real van sales from 16-18 November 2026 that the fiscalisation console flagged as
/// variances, each a cent to three away from its receipt. Each one's SAP lines were read back from SAP and its
/// receipt total from the console and ZIMRA's verification page. Every one was a sale linked to an order that
/// had less open than was sold, so <see cref="QueuedVanInvoiceLines.BaseOn"/> cut a line in two.
/// </para>
/// <para>
/// The model the tests use is checked against both sides before it is trusted: the uncut lines, totalled
/// the way the sale was, give the receipt; the cut lines at their original prices give what SAP charged.
/// Only then is the fix asked to produce the receipt.
/// </para>
/// </remarks>
public sealed class QueuedVanInvoiceLinesTests
{
    private const int OrderDocEntry = 244749;

    /// <summary>The only VAT group on these sales: O01, 15.5%.</summary>
    private const decimal VatRate = 0.155m;

    public sealed record FlaggedSale(
        string SapDocNum,
        decimal SapTotal,
        decimal Receipt,
        (string Item, decimal Quantity, decimal Price)[] Sold,
        (string Item, decimal Open)[] OrderOpen)
    {
        public override string ToString() => SapDocNum;
    }

    public static TheoryData<FlaggedSale> FlaggedSales() =>
    [
        new(
            "814591", SapTotal: 48.71m, Receipt: 48.69m,
            Sold: [("VHU002", 10m, 0.7532m), ("YOG154", 10m, 3.4632m)],
            OrderOpen: [("VHU002", 10m), ("YOG154", 5m)]),
        new(
            "814757", SapTotal: 33.28m, Receipt: 33.26m,
            Sold: [("MOZ028", 5m, 2.99m), ("YOG154", 4m, 3.4632m)],
            OrderOpen: [("MOZ028", 2m), ("YOG154", 2m)]),
        new(
            "814978", SapTotal: 171.26m, Receipt: 171.24m,
            Sold: [("YOG004", 1m, 3.8095m), ("YOG005", 1m, 3.8095m), ("YOG014", 2m, 3.8095m), ("YOG015", 1m, 3.8095m), ("YOG020", 10m, 0.8658m), ("YOG029", 6m, 5.316m), ("YOG030", 4m, 5.316m), ("YOG032", 2m, 5.316m), ("YOG053", 5m, 2.1645m), ("YOG054", 5m, 2.1645m), ("VHU002", 20m, 0.75m), ("YOG024", 10m, 0.5m), ("YOG059", 5m, 2.16m), ("YOG126", 12m, 0.36m)],
            OrderOpen: [("YOG004", 1m), ("YOG005", 1m), ("YOG014", 2m), ("YOG015", 1m), ("YOG020", 10m), ("YOG029", 3m), ("YOG030", 3m), ("YOG032", 1m), ("YOG053", 5m), ("YOG054", 5m)]),
        new(
            "815438", SapTotal: 51.89m, Receipt: 51.91m,
            Sold: [("VHU002", 20m, 0.7532m), ("YOG143", 5m, 1.039m), ("YOG144", 5m, 1.039m), ("YOG153", 2m, 3.4632m), ("YOG154", 1m, 3.4632m), ("YOG155", 2m, 3.4632m), ("YOG059", 1m, 2.16m)],
            OrderOpen: [("VHU002", 20m), ("YOG143", 5m), ("YOG144", 5m), ("YOG153", 2m), ("YOG154", 1m), ("YOG155", 1m)]),
        new(
            "816125", SapTotal: 99.97m, Receipt: 99.99m,
            Sold: [("ICC007", 36m, 0.41m), ("ICC025", 10m, 0.9091m), ("ICS016", 10m, 0.9091m), ("ICT003", 10m, 0.9091m), ("ICT005", 36m, 0.41m), ("YOG143", 6m, 1.039m), ("YOG145", 6m, 1.039m), ("YOG153", 2m, 3.4632m), ("YOG154", 2m, 3.4632m), ("YOG155", 1m, 3.4632m)],
            OrderOpen: [("ICC007", 36m), ("ICC025", 10m), ("ICS016", 10m), ("ICT003", 10m), ("ICT005", 36m), ("YOG143", 6m), ("YOG145", 6m), ("YOG153", 1m), ("YOG154", 1m), ("YOG155", 1m)]),
        new(
            "816195", SapTotal: 101.99m, Receipt: 102.02m,
            Sold: [("YOG053", 4m, 2.1645m), ("YOG054", 4m, 2.1645m), ("YOG059", 4m, 2.1645m), ("YOG143", 20m, 1.039m), ("YOG145", 20m, 1.039m), ("YOG153", 2m, 3.4632m), ("YOG154", 2m, 3.4632m), ("YOG155", 2m, 3.4632m)],
            OrderOpen: [("YOG053", 3m), ("YOG054", 3m), ("YOG059", 3m), ("YOG143", 20m), ("YOG145", 20m), ("YOG153", 2m), ("YOG154", 2m), ("YOG155", 2m)]),
        new(
            "816255", SapTotal: 84.45m, Receipt: 84.47m,
            Sold: [("VHU002", 10m, 0.7532m), ("YOG100", 24m, 0.3608m), ("YOG143", 11m, 1.039m), ("YOG144", 11m, 1.039m), ("YOG154", 3m, 3.4632m), ("YOG155", 2m, 3.4632m), ("YOG127", 24m, 0.41m), ("YOG153", 2m, 3.46m)],
            OrderOpen: [("VHU002", 10m), ("YOG100", 12m), ("YOG143", 1m), ("YOG144", 1m), ("YOG154", 1m), ("YOG155", 1m)]),
        new(
            "816386", SapTotal: 32.01m, Receipt: 31.99m,
            Sold: [("YOG154", 4m, 3.4632m), ("YOG155", 4m, 3.4632m)],
            OrderOpen: [("YOG154", 3m), ("YOG155", 2m)]),
        new(
            "816407", SapTotal: 73.59m, Receipt: 73.57m,
            Sold: [("YOG100", 36m, 0.3608m), ("YOG124", 24m, 0.4069m), ("YOG125", 24m, 0.4069m), ("YOG126", 48m, 0.3608m), ("YOG154", 4m, 3.4632m)],
            OrderOpen: [("YOG100", 36m), ("YOG124", 24m), ("YOG125", 24m), ("YOG126", 48m), ("YOG154", 2m)]),
        new(
            "816417", SapTotal: 222.95m, Receipt: 222.93m,
            Sold: [("YOG017", 10m, 0.5m), ("YOG018", 10m, 0.5m), ("YOG024", 10m, 0.5m), ("YOG027", 10m, 0.5m), ("YOG028", 10m, 0.5m), ("YOG053", 5m, 2.1645m), ("YOG054", 5m, 2.1645m), ("YOG058", 2m, 2.1645m), ("YOG059", 3m, 2.1645m), ("YOG100", 12m, 0.3608m), ("YOG101", 12m, 0.3608m), ("YOG102", 12m, 0.3608m), ("YOG126", 12m, 0.3608m), ("YOG127", 48m, 0.4069m), ("YOG143", 37m, 1.039m), ("YOG145", 38m, 1.039m), ("YOG154", 4m, 3.4632m), ("YOG155", 2m, 3.4632m)],
            OrderOpen: [("YOG017", 10m), ("YOG018", 10m), ("YOG024", 10m), ("YOG027", 10m), ("YOG028", 10m), ("YOG053", 5m), ("YOG054", 5m), ("YOG058", 2m), ("YOG059", 3m), ("YOG100", 12m), ("YOG101", 12m), ("YOG102", 12m), ("YOG126", 12m), ("YOG127", 24m), ("YOG143", 37m), ("YOG145", 38m), ("YOG154", 2m), ("YOG155", 2m)]),
    ];

    /// <summary>The model reproduces the receipt: the uncut lines, totalled as the sale was.</summary>
    [Theory]
    [MemberData(nameof(FlaggedSales))]
    public void The_uncut_lines_total_to_the_receipt(FlaggedSale sale)
    {
        var net = sale.Sold.Sum(line => RoundMoney(line.Quantity * line.Price));

        Assert.Equal(sale.Receipt, WithVat(net));
    }

    /// <summary>The model reproduces SAP: the cut lines at their original prices give what SAP charged.</summary>
    [Theory]
    [MemberData(nameof(FlaggedSales))]
    public void The_cut_lines_at_their_old_prices_total_to_what_SAP_charged(FlaggedSale sale)
    {
        var lines = Lines(sale);
        var prices = lines.ToDictionary(line => line.ItemCode!, line => line.UnitPrice!.Value);

        Assert.True(QueuedVanInvoiceLines.BaseOn(lines, Order(sale)));

        var net = lines.Sum(line => RoundMoney(line.Quantity * prices[line.ItemCode!]));
        Assert.Equal(sale.SapTotal, WithVat(net));
    }

    /// <summary>The fix: the invoice SAP is now sent comes to the receipt, to the cent.</summary>
    [Theory]
    [MemberData(nameof(FlaggedSales))]
    public void A_sale_linked_to_its_order_reaches_SAP_at_its_receipt_total(FlaggedSale sale)
    {
        var lines = Lines(sale);

        Assert.True(QueuedVanInvoiceLines.BaseOn(lines, Order(sale)));

        Assert.True(lines.Count > sale.Sold.Length, "Every one of these sales was cut by its order.");
        Assert.Equal(sale.Receipt, WithVat(lines.Sum(QueuedVanInvoiceLines.SapNetTotal)));
    }

    /// <summary>
    /// Only a cut line is re-priced, by under a cent a unit, and the order still gets exactly the quantity it
    /// had open.
    /// </summary>
    [Theory]
    [MemberData(nameof(FlaggedSales))]
    public void Only_cut_lines_move_and_by_less_than_a_cent_a_unit(FlaggedSale sale)
    {
        var lines = Lines(sale);
        QueuedVanInvoiceLines.BaseOn(lines, Order(sale));

        foreach (var (item, quantity, price) in sale.Sold)
        {
            var portions = lines.Where(line => line.ItemCode == item).ToList();

            Assert.Equal(quantity, portions.Sum(line => line.Quantity));
            Assert.All(portions, line => Assert.True(Math.Abs(line.UnitPrice!.Value - price) < 0.01m));

            if (portions.Count == 1)
            {
                Assert.Equal(price, portions[0].UnitPrice);
            }
        }

        foreach (var (item, open) in sale.OrderOpen)
        {
            Assert.Equal(open, lines.Where(line => line.ItemCode == item && line.BaseLine.HasValue).Sum(line => line.Quantity));
        }
    }

    /// <summary>The worked example in the remarks, with the batches carried through the cut.</summary>
    [Fact]
    public void Ten_units_cut_five_and_five_still_come_to_34_63_and_keep_their_batches()
    {
        var line = new CreateInvoiceLineRequest
        {
            ItemCode = "YOG154",
            Quantity = 10m,
            UnitPrice = 3.4632m,
            WarehouseCode = "VAN01",
            TaxCode = "O01",
            BatchNumbers = [new BatchNumberRequest { BatchNumber = "B1", Quantity = 6m }, new BatchNumberRequest { BatchNumber = "B2", Quantity = 4m }]
        };
        var lines = new List<CreateInvoiceLineRequest> { line };

        QueuedVanInvoiceLines.BaseOn(lines, OrderOf(("YOG154", 5m)));

        Assert.Collection(
            lines,
            linked =>
            {
                Assert.Equal(5m, linked.Quantity);
                Assert.Equal(3.4632m, linked.UnitPrice);
                Assert.Equal(0, linked.BaseLine);
                Assert.Equal(17.32m, QueuedVanInvoiceLines.SapNetTotal(linked));
                Assert.Equal(["B1:5"], linked.BatchNumbers!.Select(b => $"{b.BatchNumber}:{b.Quantity}"));
            },
            rest =>
            {
                Assert.Equal(5m, rest.Quantity);
                Assert.Null(rest.BaseLine);
                Assert.Equal(17.31m, QueuedVanInvoiceLines.SapNetTotal(rest));
                Assert.Equal(["B1:1", "B2:4"], rest.BatchNumbers!.Select(b => $"{b.BatchNumber}:{b.Quantity}"));
            });
    }

    /// <summary>
    /// The lever, swept: every way of cutting a line between one and sixty units, at every price the flagged
    /// sales carried, comes back to the uncut line's net total.
    /// </summary>
    [Fact]
    public void Any_cut_of_any_line_keeps_its_net_total()
    {
        var prices = FlaggedSales()
            .Cast<object[]>()
            .Select(row => (FlaggedSale)row[0])
            .SelectMany(sale => sale.Sold)
            .Select(line => line.Price)
            .Distinct();
        var failures = new List<string>();

        foreach (var price in prices)
        {
            for (var quantity = 2m; quantity <= 60m; quantity++)
            {
                for (var open = 1m; open < quantity; open++)
                {
                    var lines = new List<CreateInvoiceLineRequest>
                    {
                        new() { ItemCode = "X", Quantity = quantity, UnitPrice = price, WarehouseCode = "VAN01" }
                    };

                    QueuedVanInvoiceLines.BaseOn(lines, OrderOf(("X", open)));

                    var expected = RoundMoney(quantity * price);
                    var actual = lines.Sum(QueuedVanInvoiceLines.SapNetTotal);
                    if (actual != expected)
                    {
                        failures.Add($"{quantity} at {price} cut at {open}: {actual} against {expected}");
                    }
                }
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>A discounted line keeps its discounted total through the cut.</summary>
    [Fact]
    public void A_discounted_line_keeps_its_discounted_total()
    {
        var lines = new List<CreateInvoiceLineRequest>
        {
            new() { ItemCode = "X", Quantity = 7m, UnitPrice = 3.4632m, DiscountPercent = 12.5m, WarehouseCode = "VAN01" }
        };

        QueuedVanInvoiceLines.BaseOn(lines, OrderOf(("X", 3m)));

        Assert.Equal(2, lines.Count);
        Assert.Equal(RoundMoney(7m * 3.4632m * 0.875m), lines.Sum(QueuedVanInvoiceLines.SapNetTotal));
        Assert.All(lines, line => Assert.Equal(12.5m, line.DiscountPercent));
    }

    private static List<CreateInvoiceLineRequest> Lines(FlaggedSale sale) =>
        sale.Sold
            .Select(line => new CreateInvoiceLineRequest
            {
                ItemCode = line.Item,
                Quantity = line.Quantity,
                UnitPrice = line.Price,
                WarehouseCode = "VAN01",
                TaxCode = "O01"
            })
            .ToList();

    private static SAPSalesOrder Order(FlaggedSale sale) => OrderOf(sale.OrderOpen);

    private static SAPSalesOrder OrderOf(params (string Item, decimal Open)[] open) => new()
    {
        DocEntry = OrderDocEntry,
        DocumentStatus = "bost_Open",
        DocumentLines = open
            .Select((line, index) => new SAPSalesOrderLine
            {
                LineNum = index,
                ItemCode = line.Item,
                RemainingOpenQuantity = line.Open,
                LineStatus = "bost_Open"
            })
            .ToList()
    };

    /// <summary>The document's total as SAP works it: net lines plus VAT rounded once over the document.</summary>
    private static decimal WithVat(decimal net) => net + RoundMoney(net * VatRate);

    private static decimal RoundMoney(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
