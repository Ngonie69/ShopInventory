using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The margin's SAP read: single-table statements over the posted invoices' DocEntry range, never the
/// OINV-INV1 date-window join that missed the 30-second deadline on every production read (2026-10-18).
/// </summary>
public class SapSaleInvoiceCostReaderTests
{
    private readonly List<(string Warehouse, int Start, int End)> _lineReads = [];
    private readonly List<(int Start, int End)> _cancelledReads = [];
    private readonly Dictionary<string, List<SaleInvoiceLineCost>> _lines = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _cancelled = [];

    private SapSaleInvoiceCostReader Reader() => new(
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceLineCostsAsync) => LineRead((string)args![0]!, (int)args[1]!, (int)args[2]!),
            nameof(ISAPServiceLayerClient.GetCancelledInvoiceDocEntriesAsync) => CancelledRead((int)args![0]!, (int)args[1]!),
            _ => null
        }),
        NullLogger<SapSaleInvoiceCostReader>.Instance);

    private Task<List<SaleInvoiceLineCost>> LineRead(string warehouse, int start, int end)
    {
        _lineReads.Add((warehouse, start, end));
        return Task.FromResult(_lines.GetValueOrDefault(warehouse) ?? []);
    }

    private Task<HashSet<int>> CancelledRead(int start, int end)
    {
        _cancelledReads.Add((start, end));
        return Task.FromResult(_cancelled.ToHashSet());
    }

    [Fact]
    public async Task Each_warehouse_is_read_over_the_range_the_posted_invoices_span()
    {
        _lines["SHOP1"] = [new SaleInvoiceLineCost(1200, "BREAD", "SHOP1", 1m, 10m, 3m)];
        _lines["SHOP2"] = [new SaleInvoiceLineCost(1100, "MILK", "SHOP2", 1m, 5m, 1m)];

        var lines = await Reader().ReadAsync(["SHOP1", "shop1", "SHOP2", " "], [1200, 1100, 1350], CancellationToken.None);

        Assert.Equal([("SHOP1", 1100, 1350), ("SHOP2", 1100, 1350)], _lineReads);
        Assert.Equal([(1100, 1350)], _cancelledReads);
        Assert.Equal([1200, 1100], lines.Select(line => line.DocEntry));
    }

    [Fact]
    public async Task A_cancelled_invoice_carries_no_margin()
    {
        _lines["SHOP1"] =
        [
            new SaleInvoiceLineCost(1200, "BREAD", "SHOP1", 1m, 10m, 3m),
            new SaleInvoiceLineCost(1201, "BREAD", "SHOP1", 1m, 10m, 3m),
        ];
        _cancelled.Add(1201);

        var lines = await Reader().ReadAsync(["SHOP1"], [1200, 1201], CancellationToken.None);

        Assert.Equal(1200, Assert.Single(lines).DocEntry);
    }

    [Fact]
    public async Task Nothing_posted_asks_SAP_nothing()
    {
        var lines = await Reader().ReadAsync(["SHOP1"], [], CancellationToken.None);

        Assert.Empty(lines);
        Assert.Empty(_lineReads);
        Assert.Empty(_cancelledReads);
    }

    [Fact]
    public void Neither_statement_joins()
    {
        // The join under bound parameters is what this Service Layer is slow on; keep both reads to one table.
        Assert.DoesNotContain("JOIN", SAPServiceLayerClient.InvoiceLineCostSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JOIN", SAPServiceLayerClient.CancelledInvoiceSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":docEntryStart", SAPServiceLayerClient.InvoiceLineCostSql);
        Assert.Contains(":whsCode", SAPServiceLayerClient.InvoiceLineCostSql);
    }
}
