using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
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

    /// <summary>
    /// A dashboard first, then a sheet for every section of the page, then the figures the charts
    /// are drawn from.
    /// </summary>
    [Fact]
    public void The_workbook_carries_a_sheet_for_every_section()
    {
        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(Report()));

        Assert.Equal(
            ["Dashboard", "Routes", "Reps", "Items", "Price Realisation", "Trend", "Drop Size", "Chart Data"],
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    }

    /// <summary>
    /// The charts are added to the saved file with the OpenXML SDK, outside anything ClosedXML checks.
    /// A misplaced element there makes Excel "repair" the file by dropping the charts, so the package
    /// has to validate, and the charts have to be where the dashboard says they are.
    /// </summary>
    [Fact]
    public void The_dashboard_and_rep_sheet_carry_real_charts_in_a_valid_package()
    {
        var bytes = _service.ExportVanSalesPerformanceToExcel(Report());

        using var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(error => $"{error.Path?.XPath}: {error.Description}")));

        Assert.Equal(4, SheetPart(document, "Dashboard").DrawingsPart?.ChartParts.Count() ?? 0);
        Assert.Equal(2, SheetPart(document, "Reps").DrawingsPart?.ChartParts.Count() ?? 0);

        // Every chart reads the Chart Data sheet, so it redraws if a reader edits a figure there.
        var formulas = SheetPart(document, "Dashboard").DrawingsPart!.ChartParts
            .SelectMany(part => part.ChartSpace.Descendants<DocumentFormat.OpenXml.Drawing.Charts.Formula>())
            .Select(formula => formula.Text)
            .ToList();
        Assert.NotEmpty(formulas);
        Assert.All(formulas, formula => Assert.StartsWith("'Chart Data'!", formula));
    }

    /// <summary>A chart of nothing says nothing a blank space does not.</summary>
    [Fact]
    public void An_empty_period_draws_no_charts_and_still_validates()
    {
        var bytes = _service.ExportVanSalesPerformanceToExcel(new VanSalesPerformanceReportResponse
        {
            FromDate = new DateTime(2026, 8, 1),
            ToDate = new DateTime(2026, 8, 31)
        });

        using var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document));
        Assert.Null(SheetPart(document, "Dashboard").DrawingsPart);
        Assert.Null(SheetPart(document, "Reps").DrawingsPart);
    }

    /// <summary>
    /// A chart plots one currency. The figures it reads are the lead currency's alone, and a day that
    /// sold nothing is left blank so the line breaks rather than diving to zero.
    /// </summary>
    [Fact]
    public void Chart_data_is_the_lead_currency_alone_and_a_silent_day_stays_blank()
    {
        var report = Report();
        report.Trend.Daily[0].TotalsByCurrency.Add(new VanSalesMoney { Currency = "ZWG", DocumentCount = 1, DropCount = 1, Gross = 900m });

        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(report));
        var sheet = workbook.Worksheet("Chart Data");

        Assert.Equal("Takings USD", sheet.Cell(4, 2).GetString());
        Assert.Equal(610m, sheet.Cell(5, 2).GetValue<decimal>());
        Assert.True(sheet.Cell(6, 2).IsEmpty());
        Assert.True(sheet.Cell(6, 3).IsEmpty());
        Assert.DoesNotContain("900", TextOf(sheet));
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

        Assert.Equal(8, workbook.Worksheets.Count);
        Assert.Contains("VAN SALES PERFORMANCE", TextOf(workbook.Worksheet("Dashboard")));
        Assert.Contains("nothing to chart", TextOf(workbook.Worksheet("Dashboard")));
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
        var sheet = workbook.Worksheet("Routes");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "CCR").Address;
        var cell = sheet.Cell(header.RowNumber + 1, header.ColumnNumber);

        Assert.Equal("—", cell.GetFormattedString());
    }

    /// <summary>
    /// Money is a numeric column per currency. One column holding two currencies would invite a reader
    /// to total it and get a number that describes nothing; a column per currency can be totalled,
    /// filtered and charted safely. A row with nothing in a currency is blank there, not zero.
    /// </summary>
    [Fact]
    public void Two_currencies_get_a_numeric_column_each_and_never_share_one()
    {
        var report = Report();
        report.Summary.TotalsByCurrency.Add(new VanSalesMoney { Currency = "ZWG", DocumentCount = 1, DropCount = 1, Gross = 900m });
        report.Routes[0].TotalsByCurrency =
        [
            new VanSalesMoney { Currency = "USD", DocumentCount = 3, DropCount = 2, Gross = 120m },
            new VanSalesMoney { Currency = "ZWG", DocumentCount = 1, DropCount = 1, Gross = 900m }
        ];
        report.Routes.Add(new VanSalesRouteRow
        {
            HasRouteDay = true,
            RouteCode = "MVURWI",
            RouteName = "Mvurwi",
            TotalsByCurrency = [new VanSalesMoney { Currency = "USD", DocumentCount = 1, DropCount = 1, Gross = 50m }]
        });

        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(report));
        var sheet = workbook.Worksheet("Routes");

        // Last, not First: the sheet writes Territories before Routes and both carry the column.
        var usd = sheet.CellsUsed().Last(cell => cell.GetString() == "Takings USD").Address;
        var zwg = sheet.CellsUsed().Last(cell => cell.GetString() == "Takings ZWG").Address;

        Assert.Equal(XLDataType.Number, sheet.Cell(usd.RowNumber + 1, usd.ColumnNumber).DataType);
        Assert.Equal(120m, sheet.Cell(usd.RowNumber + 1, usd.ColumnNumber).GetValue<decimal>());
        Assert.Equal(900m, sheet.Cell(zwg.RowNumber + 1, zwg.ColumnNumber).GetValue<decimal>());

        // Mvurwi took no ZWG: blank, not 0.
        Assert.True(sheet.Cell(zwg.RowNumber + 2, zwg.ColumnNumber).IsEmpty());

        // The totals row adds each currency's column on its own, and follows the filter.
        var total = sheet.Cell(usd.RowNumber + 3, usd.ColumnNumber);
        Assert.Equal($"SUBTOTAL(109,{usd.ColumnLetter}{usd.RowNumber + 1}:{usd.ColumnLetter}{usd.RowNumber + 2})", total.FormulaA1);
    }

    /// <summary>
    /// A rep with no recorded calls has no strike rate, and the sheet says so with an em dash. Where
    /// there is one, it is a real percentage, so it sorts and its colour scale reads it.
    /// </summary>
    [Fact]
    public void A_strike_rate_is_a_percentage_or_an_em_dash_never_zero()
    {
        var report = Report();
        var silent = new VanSalesRepRow
        {
            UserId = Guid.NewGuid(),
            Username = "van012",
            FullName = "Farai Ncube",
            TradingDayCount = 3,
            Calls = null,
            PcrCalls = null,
            ProductiveCalls = 4,
            TotalsByCurrency = [Money("USD", 4, 4, 300m)]
        };
        report.Reps[0].PcrCalls = 50;
        report.Reps[0].PcrProductiveCalls = 40;
        report.Reps.Add(silent);

        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(report));
        var sheet = workbook.Worksheet("Reps");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Strike Rate").Address;
        var measured = sheet.Cell(header.RowNumber + 1, header.ColumnNumber);
        var unmeasured = sheet.Cell(header.RowNumber + 2, header.ColumnNumber);

        Assert.Equal(XLDataType.Number, measured.DataType);
        Assert.Equal(0.8, measured.GetValue<double>(), 3);
        Assert.Equal("—", unmeasured.GetString());
    }

    /// <summary>
    /// A gross-profit column none of whose sales is in SAP yet has nothing to add up. A SUBTOTAL over
    /// it would print 0.00, which reads as a period that made no profit; it prints an em dash.
    /// </summary>
    [Fact]
    public void A_profit_column_with_no_costed_figure_totals_to_an_em_dash_not_zero()
    {
        var report = Report();
        report.Margin = new ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport.ManagementMarginStatus { Available = true };

        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(report));
        var sheet = workbook.Worksheet("Reps");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Gross Profit USD").Address;
        Assert.Equal("not in SAP yet", sheet.Cell(header.RowNumber + 1, header.ColumnNumber).GetString());
        Assert.Equal("—", sheet.Cell(header.RowNumber + 2, header.ColumnNumber).GetString());
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

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Value USD").Address;
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
        var text = TextOf(workbook.Worksheet("Dashboard"));

        Assert.Contains("COULD NOT ANSWER", text);
        Assert.Contains("no departure record", text);
    }

    /// <summary>A clean period says nothing, so the strip's presence is itself the signal.</summary>
    [Fact]
    public void A_clean_period_writes_no_caveats_block()
    {
        using var workbook = Open(_service.ExportVanSalesPerformanceToExcel(Report()));

        Assert.DoesNotContain("COULD NOT ANSWER", TextOf(workbook.Worksheet("Dashboard")));
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
                PcrCalls = 55,
                PcrProductiveCalls = 44,
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

    private static WorksheetPart SheetPart(SpreadsheetDocument document, string name)
    {
        var sheet = document.WorkbookPart!.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
            .Single(candidate => candidate.Name == name);
        return (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!);
    }
}
