using ShopInventory.Common.Sales;

namespace ShopInventory.Tests;

/// <summary>
/// What the daily consolidated invoice says in its SAP Remarks, and that it fits the column.
/// </summary>
/// <remarks>
/// The remark listed every consolidated sale's reference with no limit, so a busy business partner
/// produced a remark far past OINV.Comments' 254 characters.
/// </remarks>
public sealed class ConsolidatedInvoiceRemarksTests
{
    private static string Reference(int number) => $"GRC-FAC-20260910-{number:D12}";

    [Fact]
    public void A_short_day_lists_every_sale()
    {
        var remarks = ConsolidatedInvoiceRemarks.Build(2, [Reference(1), Reference(2)]);

        Assert.Equal(
            "Consolidated 2 desktop sale(s): GRC-FAC-20260910-000000000001,GRC-FAC-20260910-000000000002",
            remarks);
    }

    [Fact]
    public void A_busy_day_lists_whole_references_that_fit_and_counts_the_rest()
    {
        var references = Enumerable.Range(1, 400).Select(Reference).ToList();

        var remarks = ConsolidatedInvoiceRemarks.Build(400, references);

        Assert.True(remarks.Length <= DesktopSaleInvoiceRemarks.MaxLength, $"{remarks.Length} characters");
        Assert.StartsWith("Consolidated 400 desktop sale(s): ", remarks);

        var listed = remarks["Consolidated 400 desktop sale(s): ".Length..remarks.LastIndexOf(" (+", StringComparison.Ordinal)]
            .Split(',');
        Assert.Equal(references.Take(listed.Length), listed);
        Assert.EndsWith($" (+{400 - listed.Length} more)", remarks);

        // Nothing was given up that would have fitted.
        var withOneMore = $"Consolidated 400 desktop sale(s): {string.Join(",", references.Take(listed.Length + 1))} (+{400 - listed.Length - 1} more)";
        Assert.True(withOneMore.Length > DesktopSaleInvoiceRemarks.MaxLength);
    }

    [Fact]
    public void The_whole_list_is_kept_when_it_fits_only_without_the_more_suffix()
    {
        // Short references sized so all of them fit exactly, while all but one plus " (+1 more)" does not.
        const string prefix = "Consolidated 3 desktop sale(s): ";
        var last = "Z";
        var filler = new string('A', DesktopSaleInvoiceRemarks.MaxLength - prefix.Length - 2 - 1 - last.Length);
        string[] references = [filler, "B", last];

        var remarks = ConsolidatedInvoiceRemarks.Build(3, references);

        Assert.Equal(DesktopSaleInvoiceRemarks.MaxLength, remarks.Length);
        Assert.Equal(prefix + string.Join(",", references), remarks);
    }

    [Fact]
    public void Blank_references_are_not_listed_but_the_sale_count_stays_exact()
    {
        var remarks = ConsolidatedInvoiceRemarks.Build(3, [Reference(1), null, "  "]);

        Assert.Equal("Consolidated 3 desktop sale(s): GRC-FAC-20260910-000000000001", remarks);
    }

    [Fact]
    public void A_reference_longer_than_the_column_is_counted_rather_than_cut()
    {
        var remarks = ConsolidatedInvoiceRemarks.Build(1, [new string('X', 400)]);

        Assert.Equal("Consolidated 1 desktop sale(s): (+1 more)", remarks);
    }
}
