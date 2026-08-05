using ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

namespace ShopInventory.Tests;

/// <summary>
/// Covers taking the returnable crates back out of a loaded item volume report.
/// </summary>
/// <remarks>
/// The filter subtracts each crate's own contribution from every aggregate rather than re-summing
/// what is left, which is the only way the arithmetic can be checked at all: these tests seed an
/// account total that is deliberately <em>not</em> the sum of its items, and assert the crate's
/// figure came off it exactly. A filter that re-summed would silently rewrite the API's answer.
///
/// The document counts are the exception and are asserted to stay put. An invoice that carried a
/// crate is still an invoice, and a credit note raised for nothing but returned crates is still a
/// credit note that was raised; the page says so where it prints them.
/// </remarks>
public sealed class ItemVolumeSalesCrateFilterTests
{
    [Theory]
    [InlineData("CRA001", true)]
    [InlineData("CRA006", true)]
    [InlineData("cra003", true)]
    [InlineData("CRA007", false)]
    [InlineData("CRC019", false)]
    [InlineData("FRM001", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_stated_block_counts_as_a_crate(string? itemCode, bool expected) =>
        Assert.Equal(expected, ItemVolumeSalesCrates.IsCrate(itemCode));

    [Fact]
    public void A_window_with_no_crates_is_handed_straight_back()
    {
        var report = Report(
            items: [Item("FRM001", factor: 2m, netQuantity: 100m, netVolume: 200m, usd: 480m)]);

        Assert.Same(report, ItemVolumeSalesCrates.Exclude(report));
    }

    [Fact]
    public void The_crate_rows_and_their_codes_leave_the_report()
    {
        var report = Report(
            items:
            [
                Item("FRM001", factor: 2m, netQuantity: 100m, netVolume: 200m, usd: 480m),
                Item("CRA001", factor: null, netQuantity: 40m, netVolume: 0m, usd: 60m),
                Item("CRC019", factor: null, netQuantity: 7m, netVolume: 0m, usd: 21m)
            ]);
        report.ItemCodesWithoutFactor = ["CRA001", "CRC019"];

        var filtered = ItemVolumeSalesCrates.Exclude(report);

        Assert.Equal(["FRM001", "CRC019"], filtered.ItemTotals.Select(item => item.ItemCode));

        // The notice above the figures is for gaps somebody could close. A crate is not one.
        Assert.Equal(["CRC019"], filtered.ItemCodesWithoutFactor);
    }

    [Fact]
    public void Every_aggregate_loses_exactly_the_crates_contribution()
    {
        var report = Report(
            items:
            [
                Item("FRM001", factor: 2m, netQuantity: 100m, netVolume: 200m, usd: 480m),
                Item("CRA002", factor: null, netQuantity: 40m, netVolume: 0m, usd: 60m)
            ]);

        // Deliberately not the sum of the items: the filter must subtract, not re-total.
        report.Summary.NetQuantity = 1_000m;
        report.Summary.NetVolume = 5_000m;
        report.Summary.NetRevenueUsd = 9_000m;
        report.Summary.ItemCount = 12;
        report.Summary.ItemsWithoutFactorCount = 3;
        report.Summary.QuantityWithoutFactor = 55m;

        var filtered = ItemVolumeSalesCrates.Exclude(report);

        Assert.Equal(960m, filtered.Summary.NetQuantity);
        Assert.Equal(5_000m, filtered.Summary.NetVolume);
        Assert.Equal(8_940m, filtered.Summary.NetRevenueUsd);
        Assert.Equal(11, filtered.Summary.ItemCount);
        Assert.Equal(2, filtered.Summary.ItemsWithoutFactorCount);
        Assert.Equal(15m, filtered.Summary.QuantityWithoutFactor);
    }

    [Fact]
    public void An_account_and_its_period_both_shed_the_crate()
    {
        var report = Report(
            items:
            [
                Item("FRM001", factor: 2m, netQuantity: 100m, netVolume: 200m, usd: 480m),
                Item("CRA003", factor: null, netQuantity: 40m, netVolume: 0m, usd: 60m)
            ]);

        var account = Account(
            "TMP065",
            netQuantity: 140m,
            netVolume: 200m,
            usd: 540m,
            items:
            [
                Item("FRM001", factor: 2m, netQuantity: 100m, netVolume: 200m, usd: 480m),
                Item("CRA003", factor: null, netQuantity: 40m, netVolume: 0m, usd: 60m)
            ]);

        report.AccountTotals = [account];
        report.Periods =
        [
            new ItemVolumeSalesPeriodResult
            {
                Label = "July 2026",
                PeriodStartUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                PeriodEndUtc = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc),
                InvoiceCount = 4,
                CreditNoteCount = 2,
                NetQuantity = 140m,
                NetVolume = 200m,
                NetRevenueUsd = 540m,
                Accounts = [account]
            }
        ];

        var filtered = ItemVolumeSalesCrates.Exclude(report);

        var filteredAccount = Assert.Single(filtered.AccountTotals);
        Assert.Equal(100m, filteredAccount.NetQuantity);
        Assert.Equal(480m, filteredAccount.NetRevenueUsd);
        Assert.Equal(["FRM001"], filteredAccount.Items.Select(item => item.ItemCode));
        Assert.Equal(0, filteredAccount.ItemsWithoutFactorCount);

        var period = Assert.Single(filtered.Periods);
        Assert.Equal(100m, period.NetQuantity);
        Assert.Equal(480m, period.NetRevenueUsd);
        Assert.Equal(["FRM001"], Assert.Single(period.Accounts).Items.Select(item => item.ItemCode));
    }

    [Fact]
    public void The_documents_the_crates_moved_on_are_still_counted()
    {
        var report = Report(
            items:
            [
                Item("FRM001", factor: 2m, netQuantity: 100m, netVolume: 200m, usd: 480m),
                Item("CRA001", factor: null, netQuantity: 40m, netVolume: 0m, usd: 60m)
            ]);
        report.Summary.InvoiceCount = 41;
        report.Summary.CreditNoteCount = 12;

        var filtered = ItemVolumeSalesCrates.Exclude(report);

        Assert.Equal(41, filtered.Summary.InvoiceCount);
        Assert.Equal(12, filtered.Summary.CreditNoteCount);
    }

    [Fact]
    public void The_crate_lines_leave_the_document_detail()
    {
        var report = Report(
            items:
            [
                Item("FRM001", factor: 2m, netQuantity: 100m, netVolume: 200m, usd: 480m),
                Item("CRA001", factor: null, netQuantity: 40m, netVolume: 0m, usd: 60m)
            ]);
        report.DocumentLines =
        [
            new ItemVolumeSalesDocumentLineResult { ItemCode = "FRM001", DocumentNumber = "INV-1" },
            new ItemVolumeSalesDocumentLineResult { ItemCode = "CRA001", DocumentNumber = "INV-1" }
        ];

        var filtered = ItemVolumeSalesCrates.Exclude(report);

        Assert.Equal("FRM001", Assert.Single(filtered.DocumentLines).ItemCode);
    }

    [Fact]
    public void The_loaded_report_is_left_alone_so_putting_the_crates_back_is_free()
    {
        var report = Report(
            items:
            [
                Item("FRM001", factor: 2m, netQuantity: 100m, netVolume: 200m, usd: 480m),
                Item("CRA001", factor: null, netQuantity: 40m, netVolume: 0m, usd: 60m)
            ]);
        report.Summary.NetRevenueUsd = 540m;

        ItemVolumeSalesCrates.Exclude(report);

        Assert.Equal(2, report.ItemTotals.Count);
        Assert.Equal(540m, report.Summary.NetRevenueUsd);
    }

    private static GetItemVolumeSalesReportResult Report(List<ItemVolumeSalesItemResult> items) =>
        new()
        {
            FromDateUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            ToDateUtc = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc),
            Grouping = ItemVolumeSalesGrouping.Monthly,
            RequestedAccountCodes = ["TMP065"],
            ItemTotals = items
        };

    private static ItemVolumeSalesItemResult Item(
        string itemCode,
        decimal? factor,
        decimal netQuantity,
        decimal netVolume,
        decimal usd) =>
        new()
        {
            ItemCode = itemCode,
            ItemName = itemCode + " name",
            VolumeFactor = factor,
            InvoicedQuantity = netQuantity,
            NetQuantity = netQuantity,
            InvoicedVolume = netVolume,
            NetVolume = netVolume,
            InvoicedSalesUsd = usd,
            NetRevenueUsd = usd
        };

    private static ItemVolumeSalesAccountResult Account(
        string cardCode,
        decimal netQuantity,
        decimal netVolume,
        decimal usd,
        List<ItemVolumeSalesItemResult> items) =>
        new()
        {
            CardCode = cardCode,
            CardName = cardCode + " name",
            InvoiceCount = 4,
            CreditNoteCount = 2,
            NetQuantity = netQuantity,
            NetVolume = netVolume,
            NetRevenueUsd = usd,
            ItemsWithoutFactorCount = items.Count(item => !item.HasVolumeFactor),
            Items = items
        };
}
