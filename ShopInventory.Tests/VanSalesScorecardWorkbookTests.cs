using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Generates the scorecard workbook and reads the cells back.
/// </summary>
/// <remarks>
/// The workbook is the copy that travels. Somebody opens it in a meeting having never seen the page,
/// so every disclosure the page makes has to survive the export — and the ones that matter most here
/// are the two that stop a reader over-trusting a league table: money is never ranked, and an
/// unrated row is a measurement nobody took rather than a bad one.
///
/// The band gets its own test for a reason beyond formatting. It is the only figure in this suite
/// that is a judgement about a person, and in a workbook it has to survive a photocopier, a
/// projector and a reader who cannot tell red from green — so it is written as a word, and the
/// colour is decoration on top of that rather than the message itself.
/// </remarks>
public class VanSalesScorecardWorkbookTests
{
    private readonly ReportExportService _service = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextOf(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed().Select(cell => cell.GetFormattedString()));

    /// <summary>Everything in the file, for the disclosures that may live on more than one sheet.</summary>
    private static string TextOf(XLWorkbook workbook) =>
        string.Join("\n", workbook.Worksheets.Select(TextOf));

    /// <summary>The first data cell under a column heading. Every table here writes its rows in order.</summary>
    private static IXLCell UnderHeader(IXLWorksheet sheet, string header)
    {
        var address = sheet.CellsUsed().First(cell => cell.GetString() == header).Address;
        return sheet.Cell(address.RowNumber + 1, address.ColumnNumber);
    }

    // ── Shape ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_workbook_has_an_overview_and_a_league()
    {
        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(Populated()));

        Assert.Equal(
            ["Overview", "League"],
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    }

    /// <summary>
    /// A movement figure is unreadable without knowing what it is measured against, and the workbook
    /// reader never saw the page that said so.
    /// </summary>
    [Fact]
    public void The_overview_states_the_period_it_compares_against()
    {
        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(Populated()));
        var text = TextOf(workbook.Worksheet("Overview"));

        Assert.Contains("10 Aug 2026", text);
        Assert.Contains("16 Aug 2026", text);
        Assert.Contains("Compared against 03 Aug 2026 to 09 Aug 2026", text);
        Assert.Contains("equal-length period immediately before this one", text);
    }

    // ── The disclosures ─────────────────────────────────────────────────────────

    [Fact]
    public void Every_caveat_reaches_the_file()
    {
        var report = Populated();
        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(report));
        var text = TextOf(workbook);

        // Off the report itself rather than hard-coded, so a caveat added later is covered the day
        // it is added.
        Assert.All(report.Quality.Caveats, caveat => Assert.Contains(caveat, text));
    }

    /// <summary>
    /// The two standing caveats fire in every period, so there is no such thing as a clean scorecard
    /// and the block is written unconditionally. Skipping it would itself be a claim.
    /// </summary>
    [Fact]
    public void A_clean_period_still_carries_the_standing_caveats()
    {
        var report = Populated();
        report.Quality = new VanSalesScorecardQuality
        {
            RowCount = 3,
            UnratedRows = 0,
            RowsWithNoPriorPeriod = 0,
            RowsWithNoPlan = 0,
            SalesWithoutTender = 0,
            SalesWithoutOutlet = 0,
            PriorPeriodEmpty = false
        };

        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(report));
        var text = TextOf(workbook);

        Assert.True(report.Quality.IsClean);
        Assert.Contains("WHAT THIS PERIOD COULD NOT ANSWER", text);
        Assert.Contains("never ranked", text);
        Assert.Contains("the report is right and this is a bug", text);
    }

    /// <summary>
    /// The league sheet repeats the two rules a reader most needs before reading a ranked table,
    /// because that is the sheet they will be looking at when they start ranking.
    /// </summary>
    [Fact]
    public void The_league_sheet_says_what_the_ranking_does_not_mean()
    {
        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(Populated()));
        var text = TextOf(workbook.Worksheet("League"));

        Assert.Contains("banded on their rates alone", text);
        Assert.Contains("hold no position against each other", text);
        Assert.Contains("a measurement nobody took, not a bad one", text);
    }

    // ── The band ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Written as a word, not carried by the fill alone. A photocopy, a projector and a reader who
    /// cannot distinguish red from green all lose the colour; none of them loses the word.
    /// </summary>
    [Fact]
    public void Every_band_is_written_as_a_word()
    {
        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(Populated()));
        var league = workbook.Worksheet("League");
        var text = TextOf(league);

        Assert.Contains("Green", text);
        Assert.Contains("Amber", text);
        Assert.Contains("Unrated", text);

        // And on the row itself, not only in the overview's legend.
        Assert.Equal("Green", UnderHeader(league, "Band").GetString());
    }

    [Fact]
    public void The_overview_explains_what_unrated_means()
    {
        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(Populated()));

        Assert.Contains(
            "No calls recorded, so no rate and no band. Missing, not bad.",
            TextOf(workbook.Worksheet("Overview")));
    }

    // ── Nulls and money ─────────────────────────────────────────────────────────

    /// <summary>
    /// A row with no calls has no rate and no movement. Each renders as an em dash — a zero there
    /// would report a rep who worked and sold nothing, which is a different and much worse claim.
    /// </summary>
    [Fact]
    public void A_row_with_nothing_measured_renders_dashes_and_not_zeroes()
    {
        var report = Populated();
        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(report));
        var league = workbook.Worksheet("League");

        var unratedRow = league.RowsUsed()
            .First(row => row.Cell(2).GetString() == "Unrated");

        Assert.Equal("—", unratedRow.Cell(4).GetFormattedString());   // Calls
        Assert.Equal("—", unratedRow.Cell(5).GetFormattedString());   // Strike
        Assert.Equal("—", unratedRow.Cell(6).GetFormattedString());   // Move
        Assert.Equal("—", unratedRow.Cell(7).GetFormattedString());   // Compliance
        Assert.Equal("—", unratedRow.Cell(11).GetFormattedString());  // Km
    }

    /// <summary>
    /// A currency traded this period and not last has no comparison. It says so rather than showing
    /// a movement equal to its whole takings, which would read as growth from nothing.
    /// </summary>
    [Fact]
    public void A_currency_with_no_prior_figure_says_there_is_no_comparison()
    {
        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(Populated()));
        var overview = workbook.Worksheet("Overview");
        var text = TextOf(overview);

        // The movement table's ZWG row: this period known, last period absent.
        Assert.Contains("ZWG 39,400.00", text);
        Assert.Contains("—", text);
        Assert.DoesNotContain("ZWG 0.00", text);
    }

    /// <summary>
    /// Money is text per currency so nobody can select the column and have Excel add USD to ZiG. The
    /// sum of the two is asserted absent from the whole file.
    /// </summary>
    [Fact]
    public void Two_currencies_never_become_one_number()
    {
        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(Populated()));
        var text = TextOf(workbook);

        Assert.Contains("USD 4,120.75", text);
        Assert.Contains("ZWG 39,400.00", text);

        // 4,120.75 + 39,400.00 — a figure describing nothing.
        Assert.DoesNotContain("43,520.75", text);
    }

    [Fact]
    public void A_movement_is_written_in_percentage_points()
    {
        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(Populated()));
        var text = TextOf(workbook);

        // Fleet strike moved from 70% to 75%: five points, not "+7%".
        Assert.Contains("pts", text);
        Assert.Contains("+5.0 pts", text);
    }

    // ── The empty case ──────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_period_still_produces_a_readable_workbook()
    {
        var report = new VanSalesScorecardReportResponse
        {
            FromDate = new DateTime(2026, 8, 10),
            ToDate = new DateTime(2026, 8, 16),
            PriorFromDate = new DateTime(2026, 8, 3),
            PriorToDate = new DateTime(2026, 8, 9),
            Grouping = VanSalesScorecardGrouping.Rep,
            CallComplianceTarget = 0.95,
            StrikeRateTarget = 0.75,
            Summary = new VanSalesScorecardSummary(),
            Rows = [],
            TakingsMovement = [],
            Quality = new VanSalesScorecardQuality { PriorPeriodEmpty = true }
        };

        using var workbook = Open(_service.ExportVanSalesScorecardToExcel(report));
        var text = TextOf(workbook);

        Assert.Equal(2, workbook.Worksheets.Count);
        Assert.Contains("VAN SALES SCORECARD", text);
        Assert.Contains("no van trading at all", text);
        Assert.Contains("never ranked", text);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fleet holding one of each band, two currencies and one row with nothing measured — the four
    /// shapes every assertion above needs, in one period that could plausibly have happened.
    /// </summary>
    private static VanSalesScorecardReportResponse Populated() =>
        new()
        {
            FromDate = new DateTime(2026, 8, 10),
            ToDate = new DateTime(2026, 8, 16),
            PriorFromDate = new DateTime(2026, 8, 3),
            PriorToDate = new DateTime(2026, 8, 9),
            Grouping = VanSalesScorecardGrouping.Route,
            CallComplianceTarget = 0.95,
            StrikeRateTarget = 0.75,
            Summary = new VanSalesScorecardSummary
            {
                RowCount = 3,
                GreenCount = 1,
                AmberCount = 1,
                RedCount = 0,
                UnratedCount = 1,
                TradingDays = 6,
                Calls = 100,
                CallsAgainstPlan = 95,
                PlannedCalls = 100,
                ProductiveCalls = 75,
                OutletsBought = 74,
                NewOutlets = 6,
                Kilometres = 1180,
                PriorCalls = 100,
                PriorCallsAgainstPlan = 95,
                PriorPlannedCalls = 100,
                PriorProductiveCalls = 70,
                PriorOutletsBought = 68,
                TakingsByCurrency =
                [
                    new VanSalesMoney { Currency = "USD", DocumentCount = 90, DropCount = 74, Gross = 4120.75m },
                    new VanSalesMoney { Currency = "ZWG", DocumentCount = 12, DropCount = 11, Gross = 39_400m }
                ]
            },
            Rows =
            [
                new VanSalesScorecardRow
                {
                    Key = "GURUVE", Label = "Guruve", SubLabel = "Mash Central",
                    CallComplianceTarget = 0.95, StrikeRateTarget = 0.75,
                    TradingDays = 5,
                    Calls = 40, CallsAgainstPlan = 40, PlannedCalls = 40,
                    ProductiveCalls = 32, OutletsBought = 28, NewOutlets = 3, Kilometres = 410,
                    TakingsByCurrency =
                    [
                        new VanSalesMoney { Currency = "USD", DocumentCount = 32, DropCount = 28, Gross = 1234.50m }
                    ],
                    PriorCalls = 38, PriorCallsAgainstPlan = 38, PriorPlannedCalls = 40,
                    PriorProductiveCalls = 30, PriorOutletsBought = 25,
                    PriorTakingsByCurrency =
                    [
                        new VanSalesMoney { Currency = "USD", DocumentCount = 30, DropCount = 25, Gross = 1100m }
                    ]
                },
                new VanSalesScorecardRow
                {
                    Key = "MUTOKO", Label = "Mutoko", SubLabel = "Mash East",
                    CallComplianceTarget = 0.95, StrikeRateTarget = 0.75,
                    TradingDays = 4,
                    Calls = 30, CallsAgainstPlan = 28, PlannedCalls = 30,
                    ProductiveCalls = 21, OutletsBought = 19, NewOutlets = 2, Kilometres = 380,
                    TakingsByCurrency =
                    [
                        new VanSalesMoney { Currency = "ZWG", DocumentCount = 12, DropCount = 11, Gross = 39_400m }
                    ],
                    PriorCalls = 28, PriorCallsAgainstPlan = 28, PriorPlannedCalls = 30,
                    PriorProductiveCalls = 21, PriorOutletsBought = 20,
                    PriorTakingsByCurrency = []
                },
                new VanSalesScorecardRow
                {
                    Key = "«no departure record»",
                    Label = "No departure record",
                    SubLabel = "Nothing on these sales says which route they were made on",
                    CallComplianceTarget = 0.95, StrikeRateTarget = 0.75,
                    TradingDays = 2,
                    Calls = null, CallsAgainstPlan = null, PlannedCalls = null,
                    ProductiveCalls = 4, OutletsBought = 4, NewOutlets = 0, Kilometres = null,
                    SalesWithoutTender = 1,
                    TakingsByCurrency =
                    [
                        new VanSalesMoney { Currency = "USD", DocumentCount = 4, DropCount = 4, Gross = 210.25m }
                    ],
                    PriorCalls = null, PriorCallsAgainstPlan = null, PriorPlannedCalls = null,
                    PriorProductiveCalls = 0, PriorOutletsBought = 0,
                    PriorTakingsByCurrency = []
                }
            ],
            TakingsMovement =
            [
                new VanSalesScorecardMovement { Currency = "USD", Gross = 4120.75m, PriorGross = 3800m },
                new VanSalesScorecardMovement { Currency = "ZWG", Gross = 39_400m, PriorGross = null }
            ],
            Quality = new VanSalesScorecardQuality
            {
                RowCount = 3,
                UnratedRows = 1,
                RowsWithNoPriorPeriod = 1,
                RowsWithNoPlan = 1,
                SalesWithoutTender = 1,
                SalesWithoutOutlet = 0,
                PriorPeriodEmpty = false
            }
        };
}
