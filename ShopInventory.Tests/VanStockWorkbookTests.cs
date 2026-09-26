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
    /// of this report that is somebody's job today. A depot short of stock adds its restock list.
    /// </summary>
    [Fact]
    public void The_replenishment_workbook_leads_with_the_worklist()
    {
        using var workbook = Open(_service.ExportVanReplenishmentToExcel(Replenishment()));

        Assert.Equal(["Unfilled Now", "Depot Shortages", "By Van"], workbook.Worksheets.Select(s => s.Name).ToArray());
        var worklist = TextOf(workbook.Worksheet("Unfilled Now"));
        Assert.Contains("Depot short", worklist);
        Assert.Contains("YOG100 −360", worklist);
        Assert.Contains("SAP connection closed", worklist);
        Assert.Contains("YOG100", TextOf(workbook.Worksheet("Depot Shortages")));
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
        Assert.Contains("VAN REPLENISHMENT", TextOf(workbook.Worksheet("Unfilled Now")));
    }

    // ── Stock ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_stock_workbook_carries_a_sheet_for_every_section()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));

        Assert.Equal(
            [
                "Overview", "Mornings vs SAP", "SAP Documents", "Unexplained Items", "Sales vs SAP",
                "Load & Sell-Through", "What Is Worth Carrying", "Expiry"
            ],
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
    /// The workbook has to say, on its face, that these are SAP's book figures — a reader must not
    /// take a morning that ties for a van that was physically counted.
    /// </summary>
    [Fact]
    public void The_overview_says_the_counts_are_sap_book_figures()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));
        var text = TextOf(workbook.Worksheet("Overview"));

        Assert.Contains("not the date printed on them", text);
        Assert.Contains("book figures", text);
    }

    /// <summary>
    /// Where SAP was not read there is nothing to claim either way. The counts read as unavailable —
    /// a zero would say the morning tied perfectly.
    /// </summary>
    [Fact]
    public void A_morning_sap_could_not_check_is_written_as_unavailable_not_zero()
    {
        var report = Stock();
        report.Mornings[0].SapChecked = false;

        using var workbook = Open(_service.ExportVanStockToExcel(report));
        var sheet = workbook.Worksheet("Mornings vs SAP");

        Assert.Equal("—", CellBelow(sheet, "Unexplained").GetFormattedString());
        Assert.Equal("—", CellBelow(sheet, "Ties To SAP").GetFormattedString());
    }

    /// <summary>
    /// A morning is judged in counts of items, never in a quantity summed across units, and the
    /// counts are numbers so the columns can be sorted and totalled.
    /// </summary>
    [Fact]
    public void A_morning_is_written_as_counts_of_items()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));
        var sheet = workbook.Worksheet("Mornings vs SAP");

        Assert.Equal(2, CellBelow(sheet, "Items").GetDouble());
        Assert.Equal(1, CellBelow(sheet, "Moved By SAP Documents").GetDouble());
        Assert.Equal(1, CellBelow(sheet, "Unexplained").GetDouble());
        Assert.Equal("no", CellBelow(sheet, "Ties To SAP").GetString());
    }

    /// <summary>A backdated document shows how far back it was dated, which is the whole story of a late posting.</summary>
    [Fact]
    public void A_backdated_document_carries_its_days_backdated()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));
        var sheet = workbook.Worksheet("SAP Documents");

        Assert.Equal(779350, CellBelow(sheet, "Number").GetDouble());
        Assert.Equal(2, CellBelow(sheet, "Days Backdated").GetDouble());
        Assert.Equal("04 Aug 2026 09:22", CellBelow(sheet, "Created In SAP").GetString());
    }

    /// <summary>Each unexplained item's arithmetic travels with the file, in that item's own unit.</summary>
    [Fact]
    public void The_unexplained_sheet_writes_out_each_items_arithmetic()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));
        var sheet = workbook.Worksheet("Unexplained Items");

        Assert.Equal("CHE011", CellBelow(sheet, "Item Code").GetString());
        Assert.Equal(100, CellBelow(sheet, "Earlier Count").GetDouble());
        Assert.Equal(60, CellBelow(sheet, "Invoiced").GetDouble());
        Assert.Equal(40, CellBelow(sheet, "Documents Say").GetDouble());
        Assert.Equal(31, CellBelow(sheet, "Counted").GetDouble());
        Assert.Equal(-9, CellBelow(sheet, "Unexplained").GetDouble());
    }

    [Fact]
    public void A_late_trading_day_says_how_late()
    {
        using var workbook = Open(_service.ExportVanStockToExcel(Stock()));

        Assert.Equal("2 day(s) late", CellBelow(workbook.Worksheet("Sales vs SAP"), "Verdict").GetString());
    }

    private static IXLCell CellBelow(IXLWorksheet sheet, string header)
    {
        var address = sheet.CellsUsed().First(cell => cell.GetString() == header).Address;
        return sheet.Cell(address.RowNumber + 1, address.ColumnNumber);
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

        Assert.Equal(8, workbook.Worksheets.Count);
        Assert.Contains("VAN STOCK", TextOf(workbook.Worksheet("Overview")));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static VanReplenishmentReportResponse Replenishment() => new()
    {
        FromDate = new DateTime(2026, 8, 1),
        ToDate = new DateTime(2026, 8, 31),
        GeneratedAt = new DateTime(2026, 9, 1, 8, 0, 0),
        Summary = new VanReplenishmentSummary
        {
            VanCount = 1,
            VansAsking = 1,
            RequestCount = 12,
            PostedCount = 9,
            RejectedCount = 1,
            OpenCount = 2,
            LineCount = 96,
            FilledWithinDayCount = 8,
            MedianHoursToDecision = 6,
            MedianHoursToPosting = 11,
            UnfilledNowCount = 2,
            VansWaitingNow = 1,
            OldestUnfilledDays = 25
        },
        Vans =
        [
            new VanReplenishmentVan
            {
                VanWarehouseCode = "VAN010",
                DepotWarehouses = ["KEFGRC"],
                IsAssigned = true,
                RequestCount = 12,
                PostedCount = 9,
                RejectedCount = 1,
                OpenCount = 2,
                FilledWithinDayCount = 8,
                LineCount = 96,
                TotalQuantity = 1440m,
                MedianHoursToDecision = 6,
                MedianHoursToPosting = 11,
                SlowestHoursToPosting = 52,
                LastRequestedAt = new DateTime(2026, 8, 28, 7, 0, 0),
                LastPostedAt = new DateTime(2026, 8, 28, 18, 0, 0),
                DaysSinceLastPosted = 3,
                UnfilledNowCount = 2
            }
        ],
        Unfilled =
        [
            new VanReplenishmentOpenRequest
            {
                Id = Guid.NewGuid(),
                DraftNumber = "DT-2026-00412",
                VanWarehouseCode = "VAN010",
                DepotWarehouseCode = "KEFGRC",
                Status = "PostFailed",
                Cause = VanReplenishmentCauses.DepotShort,
                RequestedBy = "Bulawayo Controller",
                RaisedByDepot = true,
                RequestedAt = new DateTime(2026, 8, 1, 16, 55, 0),
                LineCount = 25,
                ShortLineCount = 1,
                LinesInStockAtLastAttempt = 24,
                ShortItems = [new VanReplenishmentShortItem { ItemCode = "YOG100", Shortage = 360m }],
                HoursWaiting = 600,
                DaysWaiting = 25
            },
            new VanReplenishmentOpenRequest
            {
                Id = Guid.NewGuid(),
                VanWarehouseCode = "VAN010",
                DepotWarehouseCode = "KEFGRC",
                Status = "PostFailed",
                Cause = VanReplenishmentCauses.PostRefused,
                RequestedBy = "Tinashe Moyo",
                RequestedAt = new DateTime(2026, 8, 29, 7, 0, 0),
                DecidedAt = new DateTime(2026, 8, 29, 9, 0, 0),
                LineCount = 8,
                TotalQuantity = 120m,
                LastError = "SAP connection closed",
                HoursWaiting = 51.5,
                DaysWaiting = 2
            }
        ],
        DepotShortages =
        [
            new VanReplenishmentDepotShortage
            {
                DepotWarehouseCode = "KEFGRC",
                RequestCount = 1,
                VanCount = 1,
                LineCount = 25,
                LinesInStock = 24,
                Items = [new VanReplenishmentDepotShortItem { ItemCode = "YOG100", Shortage = 360m, RequestCount = 1 }]
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
            SnapshotAgeDays = 3,
            SapChecked = true
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
        Mornings =
        [
            new VanStockMorning
            {
                VanWarehouseCode = "VAN010",
                FromSnapshot = new DateTime(2026, 8, 4),
                ToSnapshot = new DateTime(2026, 8, 5),
                CountedFrom = new DateTime(2026, 8, 4, 7, 3, 0),
                CountedTo = new DateTime(2026, 8, 5, 7, 2, 0),
                GapDays = 1,
                SapChecked = true,
                ItemCount = 2,
                ItemsMoved = 1,
                ItemsUnexplained = 1,
                Documents =
                [
                    new VanStockDocument
                    {
                        Kind = "Invoice",
                        DocEntry = 2389341,
                        DocNum = 779350,
                        DocDate = new DateTime(2026, 8, 2),
                        CreatedAt = new DateTime(2026, 8, 4, 9, 22, 0),
                        CreatedTimeKnown = true,
                        ItemCount = 1
                    }
                ],
                Unexplained =
                [
                    new VanStockItemMovement
                    {
                        ItemCode = "CHE011", ItemDescription = "Cheddar 1kg",
                        Opening = 100m, Invoiced = 60m, Closing = 31m
                    }
                ]
            }
        ],
        SalesDays =
        [
            new VanStockSalesDay
            {
                VanWarehouseCode = "VAN010",
                TradingDate = new DateTime(2026, 8, 2),
                Status = "Late",
                RecordedItems = 1,
                SapItems = 1,
                SapInvoiceCount = 1,
                FirstInvoicedAt = new DateTime(2026, 8, 4, 9, 22, 0),
                LastInvoicedAt = new DateTime(2026, 8, 4, 9, 22, 0),
                NextCountAt = new DateTime(2026, 8, 3, 7, 1, 0),
                DaysLate = 2
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
