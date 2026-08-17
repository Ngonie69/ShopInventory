using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Opens the two stock workbooks and reads their cells back.
///
/// The disclosures matter more here than on the pages. A workbook gets forwarded, and whoever opens
/// it second never saw the banner that said these figures describe a morning three days ago, or the
/// note explaining that a variance across a gap is unavailable rather than nil. Both have to travel
/// with the file.
/// </summary>
public class VanStockWorkbookTests
{
    private readonly ReportExportService _service = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextOf(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed().Select(cell => cell.GetFormattedString()));

    // ── Replenishment ───────────────────────────────────────────────────────────

    /// <summary>
    /// The worklist is the first sheet, as it is the first section on the page. It is the only part
    /// of this report that is somebody's job today.
    /// </summary>
    [Fact]
    public void The_replenishment_workbook_leads_with_the_worklist()
    {
        using var workbook = Open(_service.ExportVanReplenishmentToExcel(Replenishment()));

        Assert.Equal(["Stuck Now", "By Van"], workbook.Worksheets.Select(s => s.Name).ToArray());
        Assert.Contains("SAP connection closed", TextOf(workbook.Worksheet("Stuck Now")));
    }

    /// <summary>
    /// A van that asked for nothing has no waiting time. An em dash, never a zero, which would read
    /// as a van served instantly.
    /// </summary>
    [Fact]
    public void A_van_with_no_requests_gets_an_em_dash_rather_than_zero_hours()
    {
        var report = Replenishment();
        report.Vans[0].RequestCount = 0;
        report.Vans[0].MedianHoursToDecision = null;
        report.Vans[0].MedianHoursToPosting = null;
        report.Vans[0].LastPostedAt = null;
        report.Vans[0].DaysSinceLastPosted = null;

        using var workbook = Open(_service.ExportVanReplenishmentToExcel(report));
        var sheet = workbook.Worksheet("By Van");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "To Decide").Address;
        Assert.Equal("—", sheet.Cell(header.RowNumber + 1, header.ColumnNumber).GetFormattedString());

        // And never supplied says so in words rather than showing a blank date.
        Assert.Contains("never", TextOf(sheet));
    }

    [Fact]
    public void An_empty_replenishment_period_still_produces_a_readable_workbook()
    {
        var bytes = _service.ExportVanReplenishmentToExcel(new VanReplenishmentReportResponse
        {
            FromDate = new DateTime(2026, 8, 1),
            ToDate = new DateTime(2026, 8, 31)
        });

        using var workbook = Open(bytes);

        Assert.Equal(2, workbook.Worksheets.Count);
        Assert.Contains("VAN REPLENISHMENT", TextOf(workbook.Worksheet("Stuck Now")));
    }

    // ── Stock ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_stock_workbook_carries_a_sheet_for_every_section()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));

        Assert.Equal(
            ["Overview", "Reconciliation", "Load & Sell-Through", "What Is Worth Carrying", "Expiry"],
            workbook.Worksheets.Select(s => s.Name).ToArray());
    }

    /// <summary>
    /// The staleness warning has to travel with the file. Without it, a reader opening this next week
    /// has no way to know the figures describe a morning from a fortnight ago.
    /// </summary>
    [Fact]
    public void A_stale_snapshot_warns_on_the_face_of_the_workbook()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));
        var text = TextOf(workbook.Worksheet("Overview"));

        Assert.Contains("3 DAY(S) OLD", text);
        Assert.Contains("nothing here will improve until it runs again", text);
    }

    /// <summary>A current snapshot says nothing, so the warning's presence is itself the signal.</summary>
    [Fact]
    public void A_current_snapshot_carries_no_staleness_warning()
    {
        var report = Stock();
        report.Summary.SnapshotAgeDays = 0;
        report.Quality.SnapshotAgeDays = 0;

        using var workbook = Open(_service.ExportVanStockToExcel(report));

        Assert.DoesNotContain("DAY(S) OLD", TextOf(workbook.Worksheet("Overview")));
    }

    /// <summary>
    /// Across a break in the snapshots there is nothing to expect and nothing to compare. Both cells
    /// read as unavailable — a zero variance would claim the van reconciled perfectly.
    /// </summary>
    [Fact]
    public void A_variance_across_a_gap_is_written_as_unavailable_not_zero()
    {
        var report = Stock();
        report.Variances[0].HasGap = true;
        report.Variances[0].GapDays = 2;

        using var workbook = Open(_service.ExportVanStockToExcel(report));
        var sheet = workbook.Worksheet("Reconciliation");

        var expected = sheet.CellsUsed().First(cell => cell.GetString() == "Expected").Address;
        var variance = sheet.CellsUsed().First(cell => cell.GetString() == "Variance").Address;

        Assert.Equal("—", sheet.Cell(expected.RowNumber + 1, expected.ColumnNumber).GetFormattedString());
        Assert.Equal("—", sheet.Cell(variance.RowNumber + 1, variance.ColumnNumber).GetFormattedString());
        Assert.Contains("2 days", TextOf(sheet));
    }

    /// <summary>
    /// A real variance is a number, so the column can be sorted and totalled. Only the gap rows are
    /// text.
    /// </summary>
    [Fact]
    public void A_real_variance_is_written_as_a_number()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));
        var sheet = workbook.Worksheet("Reconciliation");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Variance").Address;
        var cell = sheet.Cell(header.RowNumber + 1, header.ColumnNumber);

        Assert.Equal(XLDataType.Number, cell.DataType);
        Assert.Equal(-9, cell.GetDouble());
    }

    /// <summary>
    /// The reconciliation sheet has to explain why a gap yields nothing, because the blank rows would
    /// otherwise look like a fault in the report rather than a limit of the data.
    /// </summary>
    [Fact]
    public void The_reconciliation_sheet_explains_the_consecutive_rule()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));

        Assert.Contains("CONSECUTIVE", TextOf(workbook.Worksheet("Reconciliation")));
    }

    /// <summary>An item that never sold says so, and never with a blank date.</summary>
    [Fact]
    public void A_dead_line_is_marked_on_the_carrying_sheet()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));
        var sheet = workbook.Worksheet("What Is Worth Carrying");

        Assert.Contains("PIC003", TextOf(sheet));

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Days Idle").Address;
        Assert.Equal(20, sheet.Cell(header.RowNumber + 1, header.ColumnNumber).GetDouble());
    }

    /// <summary>An expired batch reads as past rather than as a negative number of days.</summary>
    [Fact]
    public void An_expired_batch_reads_as_past_rather_than_as_a_negative()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));

        Assert.Contains("7 past", TextOf(workbook.Worksheet("Expiry")));
    }

    [Fact]
    public void An_empty_stock_period_still_produces_a_readable_workbook()
    {
        var bytes = _service.ExportVanStockToExcel(new VanStockReportResponse
        {
            FromDate = new DateTime(2026, 8, 1),
            ToDate = new DateTime(2026, 8, 31),
            DeadStockDays = 14
        });

        using var workbook = Open(bytes);

        Assert.Equal(5, workbook.Worksheets.Count);
        Assert.Contains("VAN STOCK", TextOf(workbook.Worksheet("Overview")));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static VanReplenishmentReportResponse Replenishment() => new()
    {
        FromDate = new DateTime(2026, 8, 1),
        ToDate = new DateTime(2026, 8, 31),
        Summary = new VanReplenishmentSummary
        {
            VanCount = 1,
            RequestCount = 12,
            PostedCount = 9,
            RejectedCount = 1,
            AwaitingApprovalCount = 1,
            PostFailedCount = 1,
            LineCount = 96,
            MedianHoursToDecision = 6,
            MedianHoursToPosting = 11
        },
        Vans =
        [
            new VanReplenishmentVan
            {
                VanWarehouseCode = "VAN010",
                DepotWarehouses = ["KEFGRC"],
                RequestCount = 12,
                PostedCount = 9,
                RejectedCount = 1,
                AwaitingApprovalCount = 1,
                PostFailedCount = 1,
                LineCount = 96,
                TotalQuantity = 1440m,
                MedianHoursToDecision = 6,
                MedianHoursToPosting = 11,
                SlowestHoursToPosting = 52,
                LastRequestedAt = new DateTime(2026, 8, 28, 7, 0, 0),
                LastPostedAt = new DateTime(2026, 8, 28, 18, 0, 0),
                DaysSinceLastPosted = 3
            }
        ],
        NeedingAttention =
        [
            new VanReplenishmentRequest
            {
                Id = Guid.NewGuid(),
                VanWarehouseCode = "VAN010",
                DepotWarehouseCode = "KEFGRC",
                Status = "PostFailed",
                RequestedBy = "Tinashe Moyo",
                RequestedAt = new DateTime(2026, 8, 29, 7, 0, 0),
                DecidedAt = new DateTime(2026, 8, 29, 9, 0, 0),
                LineCount = 8,
                TotalQuantity = 120m,
                LastError = "SAP connection closed",
                HoursWaiting = 51.5
            }
        ],
        Quality = new VanReplenishmentQuality()
    };

    private static VanStockReportResponse Stock() => new()
    {
        FromDate = new DateTime(2026, 8, 1),
        ToDate = new DateTime(2026, 8, 31),
        DeadStockDays = 14,
        Summary = new VanStockSummary
        {
            VanCount = 1,
            SnapshotDayCount = 2,
            MissingSnapshotDays = 0,
            ItemCount = 1,
            DeadItemCount = 1,
            LoadedQuantity = 150m,
            SoldQuantity = 60m,
            LatestSnapshotDate = new DateTime(2026, 8, 14),
            SnapshotAgeDays = 3
        },
        Days =
        [
            new VanStockDay
            {
                VanWarehouseCode = "VAN010",
                SnapshotDate = new DateTime(2026, 8, 4),
                SnapshotComplete = true,
                ItemCount = 2,
                LoadedQuantity = 150m,
                SoldQuantity = 60m,
                AdjustmentQuantity = 0m,
                SoldItemCount = 1,
                UnsoldItemCount = 1
            }
        ],
        Variances =
        [
            new VanStockVariance
            {
                VanWarehouseCode = "VAN010",
                FromSnapshot = new DateTime(2026, 8, 4),
                ToSnapshot = new DateTime(2026, 8, 5),
                HasGap = false,
                GapDays = 1,
                OpeningQuantity = 100m,
                SoldQuantity = 60m,
                AdjustmentQuantity = 0m,
                ClosingQuantity = 31m,
                ItemsShort = 1,
                ItemsOver = 0,
                TopVariances =
                [
                    new VanStockItemVariance
                    {
                        ItemCode = "CHE011", ItemDescription = "Cheddar 1kg",
                        Expected = 40m, Actual = 31m
                    }
                ]
            }
        ],
        Items =
        [
            new VanStockItem
            {
                ItemCode = "PIC003",
                ItemDescription = "Pickles 500g",
                VanCount = 1,
                DaysOnVan = 20,
                DaysSold = 0,
                DaysOnVanWithoutSelling = 20,
                LoadedQuantity = 400m,
                SoldQuantity = 0m,
                LastSoldOn = null
            }
        ],
        Expiring =
        [
            new VanStockExpiry
            {
                VanWarehouseCode = "VAN010",
                ItemCode = "CHE011",
                ItemDescription = "Cheddar 1kg",
                BatchNumber = "BATCH-A",
                ExpiryDate = new DateTime(2026, 8, 10),
                DaysToExpiry = -7,
                Quantity = 12m,
                SnapshotDate = new DateTime(2026, 8, 14)
            }
        ],
        Quality = new VanStockQuality
        {
            LatestSnapshotDate = new DateTime(2026, 8, 14),
            SnapshotAgeDays = 3
        }
    };
}
