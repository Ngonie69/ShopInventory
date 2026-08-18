using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Opens the van sales performance workbook and reads its cells back.
///
/// The export has no seam to a database — it takes the report object and returns bytes — so the only
/// way to know it is right is to open what it produced. Everything guarded below is invisible in a
/// screenshot and decides whether a column can be trusted: whether a rate with no denominator says
/// so, whether two currencies can be accidentally added, whether a date sorts as a date.
/// </summary>
public class VanSalesPerformanceWorkbookTests
{
    private readonly ReportExportService _service = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextOf(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed().Select(cell => cell.GetFormattedString()));

    /// <summary>Every cut of the report gets a sheet, and they are named the same as the page's tabs.</summary>
    [Fact]
    public void The_workbook_carries_a_sheet_for_every_section()
    {
        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(Report()));

        Assert.Equal(
            ["Overview", "Reps", "Items", "Price Realisation", "Trend", "Drop Size"],
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    }

    /// <summary>
    /// The case that reaches production first. An empty period must not render as a header over a
    /// row of zeros, which reads as a fortnight where every van sold nothing.
    /// </summary>
    [Fact]
    public void An_empty_period_still_produces_a_readable_workbook()
    {
        var bytes = _service.ExportVanSalesPerformanceToExcel(new VanSalesPerformanceReportResponse
        {
            FromDate = new DateTime(2026, 8, 1),
            ToDate = new DateTime(2026, 8, 31)
        });

        using var workbook = Open(bytes);

        Assert.Equal(6, workbook.Worksheets.Count);
        Assert.Contains("VAN SALES PERFORMANCE", TextOf(workbook.Worksheet("Overview")));
    }

    /// <summary>
    /// A rate with no denominator has to say so in the workbook exactly as it does on the page. A
    /// 0% here would read as a route that visited nobody, which is an accusation rather than a gap.
    /// </summary>
    [Fact]
    public void A_route_with_no_plan_gets_an_em_dash_rather_than_zero_percent()
    {
        var report = Report();
        report.Routes[0].PlannedCalls = null;
        report.Routes[0].Calls = null;

        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(report));
        var sheet = workbook.Worksheet("Overview");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "CCR").Address;
        var cell = sheet.Cell(header.RowNumber + 1, header.ColumnNumber);

        Assert.Equal("—", cell.GetFormattedString());
    }

    /// <summary>
    /// Money is written as text, per currency. A numeric column carrying two currencies invites a
    /// reader to select it and watch Excel produce a total that describes nothing.
    /// </summary>
    [Fact]
    public void Two_currencies_are_written_as_text_so_they_cannot_be_summed()
    {
        var report = Report();
        report.Routes[0].TotalsByCurrency =
        [
            new VanSalesMoney { Currency = "USD", DocumentCount = 3, DropCount = 2, Gross = 120m },
            new VanSalesMoney { Currency = "ZWG", DocumentCount = 1, DropCount = 1, Gross = 900m }
        ];

        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(report));
        var sheet = workbook.Worksheet("Overview");

        // Last, not First: the Overview sheet writes Territories before Routes and both carry a
        // Takings column, so First would read the territory's total and pass on the wrong cell.
        var header = sheet.CellsUsed().Last(cell => cell.GetString() == "Takings").Address;
        var cell = sheet.Cell(header.RowNumber + 1, header.ColumnNumber);

        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.Contains("USD 120.00", cell.GetString());
        Assert.Contains("ZWG 900.00", cell.GetString());
    }

    /// <summary>
    /// The drop-size sheet is per currency by construction, so its money is a real number and a
    /// reader may total the column. That is the one place in this workbook where they can.
    /// </summary>
    [Fact]
    public void The_drop_sheet_writes_real_numbers_because_it_is_single_currency()
    {
        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(Report()));
        var sheet = workbook.Worksheet("Drop Size");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Value").Address;
        var cell = sheet.Cell(header.RowNumber + 1, header.ColumnNumber);

        Assert.Equal(XLDataType.Number, cell.DataType);
    }

    /// <summary>A date has to be a date, or the column cannot be sorted.</summary>
    [Fact]
    public void Trading_dates_are_written_as_dates_not_text()
    {
        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(Report()));
        var sheet = workbook.Worksheet("Trend");

        var header = sheet.CellsUsed().Last(cell => cell.GetString() == "Date").Address;
        var cell = sheet.Cell(header.RowNumber + 1, header.ColumnNumber);

        Assert.Equal(XLDataType.DateTime, cell.DataType);
    }

    /// <summary>
    /// The caveats have to travel with the workbook. It gets forwarded, and whoever opens it second
    /// never saw the page that explained what the figures could not see.
    /// </summary>
    [Fact]
    public void The_caveats_are_written_into_the_overview()
    {
        var report = Report();
        report.Coverage.SalesWithoutRouteDay = 4;
        report.Coverage.RepDaysWithoutRouteDay = 2;

        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(report));
        var text = TextOf(workbook.Worksheet("Overview"));

        Assert.Contains("COULD NOT ANSWER", text);
        Assert.Contains("no departure record", text);
    }

    /// <summary>A clean period says nothing, so the strip's presence is itself the signal.</summary>
    [Fact]
    public void A_clean_period_writes_no_caveats_block()
    {
        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(Report()));

        Assert.DoesNotContain("COULD NOT ANSWER", TextOf(workbook.Worksheet("Overview")));
    }

    /// <summary>
    /// The price sheet has to carry its own disclaimer. "Average 8.00" against no stated benchmark
    /// invites a reader to assume there was one.
    /// </summary>
    [Fact]
    public void The_price_sheet_states_that_it_is_peer_relative()
    {
        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(Report()));
        var text = TextOf(workbook.Worksheet("Price Realisation"));

        Assert.Contains("Peer-relative", text);
        Assert.Contains("every van line reads 0%", text);
    }

    /// <summary>
    /// A quantity travels with its unit, and van lines carry none — so the sheet has to say "unit
    /// not recorded" rather than print a bare number that reads as eaches.
    /// </summary>
    [Fact]
    public void A_quantity_with_no_unit_says_so()
    {
        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(Report()));

        Assert.Contains("unit not recorded", TextOf(workbook.Worksheet("Items")));
    }

    // --- A report with one of everything ---

    private static VanSalesPerformanceReportResponse Report() => new()
    {
        FromDate = new DateTime(2026, 8, 1),
        ToDate = new DateTime(2026, 8, 31),
        Summary = new VanSalesPerformanceSummary
        {
            RepCount = 2,
            RouteCount = 1,
            TerritoryCount = 1,
            TradingDayCount = 12,
            DocumentCount = 40,
            Calls = 55,
            ProductiveCalls = 44,
            CustomerCount = 30,
            ItemCount = 12,
            NewOutlets = 3,
            KilometresTravelled = 820,
            TotalsByCurrency = [Money("USD", 40, 33, 4200m)]
        },
        Territories =
        [
            new VanSalesTerritoryRow
            {
                Territory = "Mashonaland Central",
                RouteCount = 1,
                RepCount = 2,
                TradingDayCount = 12,
                ProductiveCalls = 44,
                CustomerCount = 30,
                TotalsByCurrency = [Money("USD", 40, 33, 4200m)]
            }
        ],
        Routes =
        [
            new VanSalesRouteRow
            {
                HasRouteDay = true,
                RouteCode = "GURUVE",
                RouteName = "Guruve",
                Territory = "Mashonaland Central",
                RepCount = 2,
                TradingDayCount = 12,
                PlannedCalls = 60,
                Calls = 55,
                ProductiveCalls = 44,
                CustomerCount = 30,
                KilometresTravelled = 820,
                TotalsByCurrency = [Money("USD", 40, 33, 4200m)]
            }
        ],
        Reps =
        [
            new VanSalesRepRow
            {
                UserId = Guid.NewGuid(),
                Username = "van010",
                FullName = "Tinashe Moyo",
                Routes = ["GURUVE"],
                TradingDayCount = 12,
                Calls = 55,
                OutletsVisited = 30,
                ProductiveCalls = 44,
                CustomerCount = 30,
                NewOutlets = 3,
                NewOutletsWhoBought = 2,
                ItemCount = 12,
                KilometresTravelled = 820,
                TotalsByCurrency = [Money("USD", 40, 33, 4200m)]
            }
        ],
        Items =
        [
            new VanSalesItemRow
            {
                Rank = 1,
                ItemCode = "CHE011",
                ItemDescription = "Cheddar 1kg",
                LineCount = 22,
                DocumentCount = 20,
                CustomerCount = 18,
                RepCount = 2,
                TradingDayCount = 11,
                FirstSoldOn = new DateTime(2026, 8, 2),
                LastSoldOn = new DateTime(2026, 8, 29),
                // No unit, which is what every van line looks like today.
                QuantitiesByUoM = [new VanSalesQuantity { UoMCode = null, Quantity = 260m, LineCount = 22 }],
                TotalsByCurrency = [new VanSalesLineMoney { Currency = "USD", LineCount = 22, Gross = 2600m }]
            }
        ],
        LapsedItems =
        [
            new VanSalesLapsedItemRow
            {
                ItemCode = "PIC003",
                ItemDescription = "Pickles 500g",
                LastSoldOn = new DateTime(2026, 7, 20),
                DaysSinceLastSale = 42,
                PriorLineCount = 9,
                PriorCustomerCount = 7,
                PriorTotalsByCurrency = [new VanSalesLineMoney { Currency = "USD", LineCount = 9, Gross = 310m }]
            }
        ],
        Trend = new VanSalesTrend
        {
            Daily =
            [
                new VanSalesTrendPoint
                {
                    TradingDate = new DateTime(2026, 8, 3),
                    DayOfWeek = DayOfWeek.Monday,
                    RepsTrading = 2,
                    ProductiveCalls = 6,
                    DocumentCount = 7,
                    TotalsByCurrency = [Money("USD", 7, 6, 610m)]
                },
                new VanSalesTrendPoint
                {
                    TradingDate = new DateTime(2026, 8, 4),
                    DayOfWeek = DayOfWeek.Tuesday,
                    RepsTrading = 0,
                    ProductiveCalls = 0,
                    DocumentCount = 0,
                    TotalsByCurrency = []
                }
            ],
            DayOfWeek =
            [
                new VanSalesSeasonPoint
                {
                    Label = "Monday",
                    DayOfWeek = DayOfWeek.Monday,
                    CalendarDayCount = 5,
                    ActiveDayCount = 4,
                    DocumentCount = 20,
                    ProductiveCalls = 18,
                    TotalsByCurrency = [Money("USD", 20, 18, 2100m)]
                }
            ],
            Monthly =
            [
                new VanSalesSeasonPoint
                {
                    Label = "Aug 2026",
                    Year = 2026,
                    Month = 8,
                    IsPartial = false,
                    CalendarDayCount = 31,
                    ActiveDayCount = 12,
                    DocumentCount = 40,
                    ProductiveCalls = 44,
                    TotalsByCurrency = [Money("USD", 40, 33, 4200m)]
                }
            ]
        },
        ItemPrices =
        [
            new VanSalesItemPriceRow
            {
                ItemCode = "CHE011",
                ItemDescription = "Cheddar 1kg",
                Currency = "USD",
                UoMCode = null,
                LineCount = 22,
                Quantity = 260m,
                Gross = 2600m,
                WeightedAveragePrice = 10m,
                MinUnitPrice = 8m,
                MaxUnitPrice = 11m,
                Reps =
                [
                    new VanSalesRepPriceRow
                    {
                        UserId = Guid.NewGuid(),
                        Username = "van011",
                        FullName = "Rudo Chikanga",
                        LineCount = 6,
                        Quantity = 60m,
                        Gross = 480m,
                        WeightedAveragePrice = 8m,
                        VarianceFromItemPercent = -20m
                    }
                ]
            }
        ],
        DropSizes =
        [
            new VanSalesDropSize
            {
                Currency = "USD",
                DropCount = 33,
                Total = 4200m,
                Minimum = 4m,
                P25 = 40m,
                Median = 95m,
                P75 = 180m,
                Maximum = 640m,
                Mean = 127.27m,
                Buckets =
                [
                    new VanSalesDropSizeBucket
                    {
                        Label = "0–5",
                        LowerBound = 0m,
                        UpperBound = 5m,
                        DropCount = 2,
                        Total = 8m,
                        SharePercent = 0.19
                    },
                    new VanSalesDropSizeBucket
                    {
                        Label = "100+",
                        LowerBound = 100m,
                        UpperBound = null,
                        DropCount = 14,
                        Total = 3400m,
                        SharePercent = 80.95
                    }
                ]
            }
        ],
        Coverage = new VanSalesCoverage { SaleCount = 40, LineCount = 96 }
    };

    private static VanSalesMoney Money(string currency, int documents, int drops, decimal gross) =>
        new() { Currency = currency, DocumentCount = documents, DropCount = drops, Gross = gross };
}
