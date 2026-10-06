using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The return slip printed for a breakage report once SAP has moved it into returns. It travels with
/// the stock, so it has to name the SAP transfer, carry the office's count beside the rep's, and never
/// pass off a line nobody counted as zero. Set <c>BREAKAGE_SLIP_OUT</c> to a folder to keep the HTML
/// for printing with headless Chrome.
/// </summary>
public sealed class MarketBreakageDocumentBuilderTests
{
    private static readonly DateTime PrintedAtCat = new(2026, 10, 6, 9, 30, 0);

    [Fact]
    public void A_transferred_report_names_its_SAP_transfer_and_both_warehouses()
    {
        var html = Build(Transferred(), "transferred");

        Assert.Contains("Report #3", html);
        Assert.Contains("Transferred", html);
        Assert.Contains("VAN002 → RET-01", html);
        Assert.Contains("10457", html);   // SAP DocNum
        Assert.Contains("88213", html);   // SAP DocEntry
        Assert.Contains("Moved to returns", html);
        Assert.Contains("mod-stat-value mod-num\">164<", html);
        Assert.Contains("Received into RET-01 by", html);
    }

    [Fact]
    public void A_short_count_shows_on_its_line_and_in_the_totals()
    {
        var html = Build(Transferred(), "short");

        Assert.Contains("−2 short", html);
        Assert.Contains("Matches", html);
    }

    [Fact]
    public void An_uncounted_line_reads_not_counted_rather_than_zero()
    {
        var report = Transferred();
        report.Status = MarketBreakageStatus.Pending;
        report.SapDocNum = null;
        report.SapDocEntry = null;
        report.TransferredAtUtc = null;
        foreach (var line in report.Lines)
            line.ConfirmedQuantity = null;

        var html = Build(report, "uncounted");

        Assert.Contains("Not counted", html);
        Assert.Contains("Not posted", html);
        Assert.Contains("Units counted", html);
        Assert.DoesNotContain("Moved to returns", html);
    }

    [Fact]
    public void Text_the_rep_typed_is_escaped()
    {
        var report = Transferred();
        report.Remarks = "<script>alert(1)</script> & two torn";

        var html = Build(report, "escaped");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; two torn", html);
    }

    private static string Build(MarketBreakageDetailDto report, string name)
    {
        var html = MarketBreakageDocumentBuilder.Build(report, PrintedAtCat);

        if (Environment.GetEnvironmentVariable("BREAKAGE_SLIP_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"breakage-slip-{name}.html"), html);
        }

        return html;
    }

    private static MarketBreakageDetailDto Transferred() => new()
    {
        Id = 3,
        Status = MarketBreakageStatus.Transferred,
        ReportedByName = "Learnmore Zogara",
        VanWarehouseCode = "VAN002",
        CardCode = "CS00412",
        CardName = "OK Mart Borrowdale",
        Remarks = "Cooler failed on the Borrowdale run, tubs soft on arrival.",
        CapturedAtUtc = new DateTime(2026, 9, 27, 6, 48, 0, DateTimeKind.Utc),
        DecidedByName = "Tendai Moyo",
        DecidedAtUtc = new DateTime(2026, 10, 6, 7, 12, 0, DateTimeKind.Utc),
        DecisionRemarks = "Two packs were resealable and went back to the van.",
        ReturnsWarehouseCode = "RET-01",
        SapDocEntry = 88213,
        SapDocNum = 10457,
        TransferredAtUtc = new DateTime(2026, 10, 6, 7, 12, 30, DateTimeKind.Utc),
        Lines =
        [
            new() { Id = 1, LineNum = 0, ItemCode = "YOG144", ItemDescription = "6 Pack Vanilla Dairy Scoop Snack", Reason = "Damaged", ReportedQuantity = 150, ConfirmedQuantity = 148 },
            new() { Id = 2, LineNum = 1, ItemCode = "CHS020", ItemDescription = "Gouda Wedge 200g", Reason = "Expired", ReportedQuantity = 16, ConfirmedQuantity = 16 }
        ]
    };
}
