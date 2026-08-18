using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Opens the coverage workbook and reads its cells back.
///
/// The two disclosures this report carries — that the uncovered register is measured against today's
/// roster, and that the location sheet is not a geofence — matter more in a workbook than on the
/// page. A workbook gets forwarded, and whoever opens it second never saw the page that explained
/// what the figures could and could not mean. These check both of them survive the export.
/// </summary>
public class VanSalesCoverageWorkbookTests
{
    private readonly ReportExportService _service = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextOf(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed().Select(cell => cell.GetFormattedString()));

    [Fact]
    public void The_workbook_carries_a_sheet_for_every_section()
    {
        using var workbook = Open(_service.ExportVanSalesCoverageToExcel(Report()));

        Assert.Equal(
            ["Overview", "Reps", "Not Reached", "Churn", "Concentration", "Shop Behaviour", "Location"],
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    }

    /// <summary>The case that reaches production first.</summary>
    [Fact]
    public void An_empty_period_still_produces_a_readable_workbook()
    {
        var bytes = _service.ExportVanSalesCoverageToExcel(new VanSalesCoverageReportResponse
        {
            FromDate = new DateTime(2026, 8, 1),
            ToDate = new DateTime(2026, 8, 31),
            LapseDays = 90
        });

        using var workbook = Open(bytes);

        Assert.Equal(7, workbook.Worksheets.Count);
        Assert.Contains("VAN SALES COVERAGE", TextOf(workbook.Worksheet("Overview")));
    }

    /// <summary>
    /// The register is only as good as the roster it was measured against, and that roster is today's.
    /// A reader who does not know that will read a shop added last week as one missed all quarter.
    /// </summary>
    [Fact]
    public void The_live_roster_disclosure_is_written_onto_the_register_itself()
    {
        using var workbook = Open(_service.ExportVanSalesCoverageToExcel(Report()));

        var text = TextOf(workbook.Worksheet("Not Reached"));

        Assert.Contains("TODAY'S roster", text);
        Assert.Contains("absent entirely", text);
    }

    /// <summary>
    /// The strongest thing this report has to say about itself. Without it, a column headed "Real
    /// Fix" invites exactly the conclusion the data cannot support.
    /// </summary>
    [Fact]
    public void The_location_sheet_states_that_it_is_not_a_geofence()
    {
        using var workbook = Open(_service.ExportVanSalesCoverageToExcel(Report()));

        Assert.Contains("NOT A GEOFENCE", TextOf(workbook.Worksheet("Location")));
    }

    /// <summary>
    /// A rate with no denominator has to read as unavailable in the workbook exactly as it does on
    /// the page. A 0% coverage figure against an unknown roster would be an accusation.
    /// </summary>
    [Fact]
    public void A_rep_with_no_roster_gets_an_em_dash_rather_than_zero_percent()
    {
        var report = Report();
        report.Reps[0].RosterSize = null;
        report.Reps[0].OutletsVisited = null;

        using var workbook = Open(_service.ExportVanSalesCoverageToExcel(report));
        var sheet = workbook.Worksheet("Reps");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Coverage").Address;
        var cell = sheet.Cell(header.RowNumber + 1, header.ColumnNumber);

        Assert.Equal("—", cell.GetFormattedString());
    }

    /// <summary>
    /// The churn residual is written out so a fault in the arithmetic is visible in the workbook too,
    /// not only on the page. It should always be zero.
    /// </summary>
    [Fact]
    public void The_churn_residual_is_carried_into_the_workbook()
    {
        using var workbook = Open(_service.ExportVanSalesCoverageToExcel(Report()));
        var sheet = workbook.Worksheet("Churn");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Residual").Address;
        var cell = sheet.Cell(header.RowNumber + 1, header.ColumnNumber);

        Assert.Equal(XLDataType.Number, cell.DataType);
        Assert.Equal(0, cell.GetDouble());
    }

    /// <summary>
    /// A shop that has never bought says so in words. A blank or a zero date would read as one that
    /// bought at the dawn of time, which is the opposite of the truth.
    /// </summary>
    [Fact]
    public void A_shop_that_never_bought_says_never_rather_than_showing_a_date()
    {
        var report = Report();
        report.UncoveredOutlets[0].LastPurchaseOn = null;
        report.UncoveredOutlets[0].DaysSinceLastPurchase = null;

        using var workbook = Open(_service.ExportVanSalesCoverageToExcel(report));

        Assert.Contains("never", TextOf(workbook.Worksheet("Not Reached")));
    }

    /// <summary>Money stays text and per currency, so two currencies cannot be summed by accident.</summary>
    [Fact]
    public void Money_is_written_as_text_per_currency()
    {
        var report = Report();
        report.LapsedOutlets[0].PriorTotalsByCurrency =
        [
            new VanSalesMoney { Currency = "USD", DocumentCount = 20, DropCount = 14, Gross = 900m },
            new VanSalesMoney { Currency = "ZWG", DocumentCount = 4, DropCount = 3, Gross = 4200m }
        ];

        using var workbook = Open(_service.ExportVanSalesCoverageToExcel(report));
        var sheet = workbook.Worksheet("Churn");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Worth Before").Address;
        var cell = sheet.Cell(header.RowNumber + 1, header.ColumnNumber);

        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.Contains("USD 900.00", cell.GetString());
        Assert.Contains("ZWG 4,200.00", cell.GetString());
    }

    // --- A report with one of everything ---

    private static VanSalesCoverageReportResponse Report() => new()
    {
        FromDate = new DateTime(2026, 8, 1),
        ToDate = new DateTime(2026, 8, 31),
        PriorWindowFrom = new DateTime(2026, 5, 3),
        LapseDays = 90,
        Granularity = VanSalesCoverageGranularity.Month,
        Summary = new VanSalesCoverageSummary
        {
            RepCount = 1,
            RosterSize = 120,
            OutletsVisited = 95,
            OutletsBought = 80,
            OutletsUncovered = 25,
            NewOutlets = 6,
            ReactivatedOutlets = 3,
            LapsedOutlets = 4,
            Calls = 100,
            ProductiveCalls = 80,
            PlannedCalls = 120,
            KilometresTravelled = 1200,
            TotalsByCurrency = [Money("USD", 260, 210, 8400m)]
        },
        Trend =
        [
            new VanSalesCoverageTrendPoint
            {
                Label = "Aug 2026",
                BucketStart = new DateTime(2026, 8, 1),
                BucketEnd = new DateTime(2026, 8, 31),
                RepsTrading = 1,
                PlannedCalls = 120,
                Calls = 100,
                ProductiveCalls = 80,
                OutletsBought = 80
            }
        ],
        Reps =
        [
            new VanSalesRepCoverage
            {
                UserId = Guid.NewGuid(),
                Username = "van010",
                FullName = "Tinashe Moyo",
                VanAccountCode = "VAN010",
                Routes = ["GURUVE"],
                OutletsAttributable = true,
                RosterSize = 120,
                TradingDayCount = 22,
                Calls = 100,
                OutletsVisited = 95,
                ProductiveCalls = 80,
                OutletsBought = 80,
                OutletsUncovered = 25,
                PlannedCalls = 120,
                KilometresTravelled = 1200,
                EfficiencyByCurrency =
                [
                    new VanSalesEfficiency
                    {
                        Currency = "USD", Gross = 8400m, DropCount = 210,
                        Kilometres = 1200, DaysWithMileage = 20, DaysWithoutMileage = 2
                    }
                ],
                TotalsByCurrency = [Money("USD", 260, 210, 8400m)]
            }
        ],
        UncoveredOutlets =
        [
            new VanSalesUncoveredOutlet
            {
                VanAccountCode = "VAN010",
                OutletCode = "TUCK01",
                OutletName = "Tuck Shop",
                Phone = "0772 000 000",
                Gap = VanSalesCoverageGap.NotVisitedInWindow,
                LastVisitedOn = new DateTime(2026, 6, 12),
                LastPurchaseOn = new DateTime(2026, 6, 12),
                DaysSinceLastPurchase = 80,
                CapturedOn = new DateTime(2026, 1, 5),
                OwningReps = ["Tinashe Moyo"]
            }
        ],
        LocationIntegrity = new VanSalesLocationIntegrity
        {
            CallCount = 100,
            CallsWithGpsFix = 75,
            CallsWithLastKnownFix = 15,
            CallsWithNoFix = 10,
            CallsWithoutAccuracy = 5,
            CallsWithPoorAccuracy = 8,
            CallsCapturedOffline = 12,
            PoorAccuracyMetres = 250,
            Reps =
            [
                new VanSalesRepLocation
                {
                    UserId = Guid.NewGuid(),
                    Username = "van010",
                    FullName = "Tinashe Moyo",
                    CallCount = 100,
                    CallsWithGpsFix = 75,
                    CallsWithLastKnownFix = 15,
                    CallsWithNoFix = 10,
                    CallsWithPoorAccuracy = 8,
                    CallsCapturedOffline = 12
                }
            ]
        },
        Churn =
        [
            new VanSalesChurnPoint
            {
                Label = "Aug 2026",
                BucketStart = new DateTime(2026, 8, 1),
                BucketEnd = new DateTime(2026, 8, 31),
                OpeningActiveOutlets = 97,
                BuyingOutlets = 80,
                NewOutlets = 6,
                ReactivatedOutlets = 3,
                LapsedOutlets = 4,
                ClosingActiveOutlets = 102,
                TotalsByCurrency = [Money("USD", 260, 210, 8400m)],
                NewOutletTotalsByCurrency = [Money("USD", 12, 10, 340m)]
            }
        ],
        LapsedOutlets =
        [
            new VanSalesLapsedOutlet
            {
                VanAccountCode = "VAN010",
                OutletCode = "GONE1",
                OutletName = "Used To Buy",
                Phone = "0772 111 111",
                LastPurchaseOn = new DateTime(2026, 5, 2),
                DaysSinceLastPurchase = 121,
                LastVisitedOn = new DateTime(2026, 5, 2),
                LastSoldByRep = "Tinashe Moyo",
                LastRouteCode = "GURUVE",
                StillOnRoster = true,
                PriorPurchaseDayCount = 14,
                LastPurchaseByCurrency = [Money("USD", 2, 1, 100m)],
                PriorTotalsByCurrency = [Money("USD", 20, 14, 900m)],
                OwningReps = ["Tinashe Moyo"]
            }
        ],
        Concentration =
        [
            new VanSalesConcentration
            {
                HasRouteDay = true,
                RouteCode = "GURUVE",
                RouteName = "Guruve",
                Currency = "USD",
                OutletCount = 40,
                AttributedGross = 8000m,
                UnattributedGross = 400m,
                Top1Gross = 4000m,
                Top5Gross = 6000m,
                Top10Gross = 7000m,
                OutletsForHalfOfGross = 1,
                TopOutlets =
                [
                    new VanSalesOutletShare { Rank = 1, OutletCode = "BIG1", OutletName = "Big Shop", Gross = 4000m, SharePercent = 50 }
                ]
            }
        ],
        Outlets =
        [
            new VanSalesOutletActivity
            {
                VanAccountCode = "VAN010",
                OutletCode = "TUCK02",
                OutletName = "Busy Shop",
                VisitCount = 8,
                PurchaseDayCount = 4,
                DocumentCount = 5,
                DistinctItemCount = 12,
                AverageItemsPerPurchase = 3.5m,
                FirstPurchaseInWindow = new DateTime(2026, 8, 3),
                LastPurchaseInWindow = new DateTime(2026, 8, 27),
                FirstEverPurchaseOn = new DateTime(2025, 2, 1),
                AverageDaysBetweenPurchases = 8.0,
                TotalsByCurrency = [Money("USD", 5, 4, 420m)]
            }
        ],
        Quality = new VanSalesCoverageQuality { SaleCount = 260, RosterIsLive = true }
    };

    private static VanSalesMoney Money(string currency, int documents, int drops, decimal gross) =>
        new() { Currency = currency, DocumentCount = documents, DropCount = drops, Gross = gross };
}
