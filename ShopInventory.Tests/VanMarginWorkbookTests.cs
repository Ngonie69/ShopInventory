using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Generates the margin workbook and reads the cells back.
/// </summary>
/// <remarks>
/// The workbook is the copy that travels, and this is the one report in the suite where that matters
/// most: it is headed "margin" and does not have one. Somebody opens the file in a meeting having
/// never seen the page, sees a sheet of money under that heading, and reads it as margin. So the
/// absence is written first, in the bad colour, before any figure — and these tests hold it there.
///
/// The cost and margin columns are written and empty rather than omitted, and that is also pinned. A
/// missing column invites a reader to work one out from the revenue beside it; a column of dashes
/// under a heading says the figure is coming and is not here yet.
/// </remarks>
public class VanMarginWorkbookTests
{
    private readonly ReportExportService _service = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextOf(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed().Select(cell => cell.GetFormattedString()));

    private static string TextOf(XLWorkbook workbook) =>
        string.Join("\n", workbook.Worksheets.Select(TextOf));

    private static IXLCell UnderHeader(IXLWorksheet sheet, string header)
    {
        var address = sheet.CellsUsed().First(cell => cell.GetString() == header).Address;
        return sheet.Cell(address.RowNumber + 1, address.ColumnNumber);
    }

    // ── Shape ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_workbook_has_an_overview_an_item_sheet_and_a_van_sheet()
    {
        using var workbook = Open(_service.ExportVanMarginToExcel(Populated()));

        Assert.Equal(
            ["Overview", "Items", "Vans"],
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    }

    // ── The absence ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The scope of the margin is stated before any figure. A reader who meets numbers first under a
    /// heading of "margin" reads all of them as margins, and most of them are revenue over a wider
    /// population than the margin covers.
    /// </summary>
    [Fact]
    public void The_overview_states_the_scope_of_the_margin_before_any_figure()
    {
        using var workbook = Open(_service.ExportVanMarginToExcel(Populated()));
        var overview = workbook.Worksheet("Overview");

        var statement = overview.CellsUsed()
            .First(cell => cell.GetString().Contains("Margin is stated in USD only"));

        var firstFigure = overview.CellsUsed()
            .First(cell => cell.GetString().Contains("1,000.00"));

        Assert.True(
            statement.Address.RowNumber < firstFigure.Address.RowNumber,
            "the scope of the margin must be stated above the first money figure");

        // And it names the share, because a margin over 40% of the lines is not the period margin.
        Assert.Contains("40%", statement.GetString());
    }

    /// <summary>
    /// With no cost established the sheet says so in the loud colour instead, and says nothing that
    /// could be read as a margin.
    /// </summary>
    [Fact]
    public void A_period_with_no_cost_says_margin_is_not_computed()
    {
        var report = Populated();
        report.Summary.CostCurrency = null;
        report.Summary.MarginByCurrency = [];
        report.Quality.CostCurrency = null;

        using var workbook = Open(_service.ExportVanMarginToExcel(report));

        Assert.Contains("MARGIN IS NOT COMPUTED", TextOf(workbook.Worksheet("Overview")));
        Assert.Contains("No cost could be read from SAP", TextOf(workbook));
    }

    /// <summary>
    /// A costed item shows its cost and margin; an uncosted one shows an em dash in both. The dash
    /// is the whole point — a zero there would report an item that costs nothing to sell.
    /// </summary>
    [Fact]
    public void A_costed_item_shows_its_margin_and_an_uncosted_one_shows_a_dash()
    {
        using var workbook = Open(_service.ExportVanMarginToExcel(Populated()));
        var items = workbook.Worksheet("Items");

        // Ranked on revenue, so the costed item leads.
        Assert.Equal("USD 8.2500", UnderHeader(items, "Unit cost").GetFormattedString());
        Assert.Equal("USD 140.00", UnderHeader(items, "Margin").GetFormattedString());

        var uncosted = items.RowsUsed().First(row => row.Cell(1).GetString() == "NRI049");
        Assert.Equal("—", uncosted.Cell(8).GetFormattedString());
        Assert.Equal("—", uncosted.Cell(9).GetFormattedString());
    }

    /// <summary>
    /// A loss is a finding rather than an error, and the workbook has to carry the sign rather than
    /// losing it somewhere in formatting.
    /// </summary>
    [Fact]
    public void An_item_sold_below_cost_reports_a_negative_margin()
    {
        var report = Populated();
        report.Items[0].MarginByCurrency =
            [new VanMarginMoney { Currency = "USD", LineCount = 3, Revenue = 350m, Cost = 420m }];

        using var workbook = Open(_service.ExportVanMarginToExcel(report));

        Assert.Contains("USD -70.00", TextOf(workbook.Worksheet("Items")));
    }

    [Fact]
    public void Every_caveat_reaches_the_file()
    {
        var report = Populated();
        using var workbook = Open(_service.ExportVanMarginToExcel(report));
        var text = TextOf(workbook);

        // Off the report itself, so a caveat added later is covered the day it is added.
        Assert.All(report.Quality.Caveats, caveat => Assert.Contains(caveat, text));
    }

    // ── The posting switch ──────────────────────────────────────────────────────

    [Fact]
    public void The_posting_switch_banner_appears_only_when_the_job_is_off()
    {
        var off = Populated();
        using (var workbook = Open(_service.ExportVanMarginToExcel(off)))
        {
            Assert.Contains("posting job is switched off", TextOf(workbook), StringComparison.OrdinalIgnoreCase);
        }

        var on = Populated();
        on.Quality = new VanMarginQuality
        {
            LineCount = 10,
            PostedLineCount = 10,
            ItemsWithNoDescription = 0,
            PostingJobEnabled = true
        };

        using var running = Open(_service.ExportVanMarginToExcel(on));
        Assert.DoesNotContain("switched off", TextOf(running), StringComparison.OrdinalIgnoreCase);
    }

    // ── Money and nulls ─────────────────────────────────────────────────────────

    /// <summary>
    /// Revenue and costable revenue are two figures and must stay apart. A reader comparing them is
    /// reading the whole point of the report.
    /// </summary>
    [Fact]
    public void Revenue_and_costable_revenue_are_reported_separately()
    {
        using var workbook = Open(_service.ExportVanMarginToExcel(Populated()));
        var text = TextOf(workbook);

        Assert.Contains("USD 1,000.00", text);
        Assert.Contains("USD 400.00", text);
        // Not folded into one figure.
        Assert.DoesNotContain("USD 1,400.00", text);
    }

    [Fact]
    public void Two_currencies_never_become_one_number()
    {
        var report = Populated();
        report.Summary.RevenueByCurrency =
        [
            new VanSalesLineMoney { Currency = "USD", LineCount = 6, Gross = 620m },
            new VanSalesLineMoney { Currency = "ZWG", LineCount = 4, Gross = 38_000m }
        ];

        using var workbook = Open(_service.ExportVanMarginToExcel(report));
        var text = TextOf(workbook);

        Assert.Contains("USD 620.00", text);
        Assert.Contains("ZWG 38,000.00", text);
        Assert.DoesNotContain("38,620.00", text);
    }

    /// <summary>
    /// An item nobody described is reported by its code rather than dropped, with an em dash where
    /// the description would be.
    /// </summary>
    [Fact]
    public void An_item_with_no_description_still_gets_a_row()
    {
        using var workbook = Open(_service.ExportVanMarginToExcel(Populated()));
        var text = TextOf(workbook.Worksheet("Items"));

        Assert.Contains("NRI049", text);
        Assert.Contains("item(s) sold under a code with no description", TextOf(workbook));
    }

    /// <summary>
    /// Quantity is never totalled across units. Van lines carry no unit, so the sheet says so rather
    /// than printing a bare number.
    /// </summary>
    [Fact]
    public void Quantity_names_its_unit_even_when_there_is_not_one()
    {
        using var workbook = Open(_service.ExportVanMarginToExcel(Populated()));

        Assert.Contains("unit not recorded", TextOf(workbook.Worksheet("Overview")));
    }

    // ── The empty case ──────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_period_still_produces_a_readable_workbook()
    {
        var report = new VanMarginReportResponse
        {
            FromDate = new DateTime(2026, 8, 1),
            ToDate = new DateTime(2026, 8, 31),
            Summary = new VanMarginSummary(),
            Items = [],
            Vans = [],
            Quality = new VanMarginQuality()
        };

        using var workbook = Open(_service.ExportVanMarginToExcel(report));
        var text = TextOf(workbook);

        Assert.Equal(3, workbook.Worksheets.Count);
        Assert.Contains("VAN MARGIN", text);
        Assert.Contains("MARGIN IS NOT COMPUTED", text);
        // The share is unavailable rather than zero — nothing sold, so nothing failed to post.
        Assert.Contains("—", text);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────

    private static VanMarginReportResponse Populated() =>
        new()
        {
            FromDate = new DateTime(2026, 8, 1),
            ToDate = new DateTime(2026, 8, 31),
            Summary = new VanMarginSummary
            {
                ItemCount = 2,
                VanCount = 2,
                LineCount = 10,
                PostedLineCount = 4,
                RevenueByCurrency =
                    [new VanSalesLineMoney { Currency = "USD", LineCount = 10, Gross = 1000m }],
                CostableRevenueByCurrency =
                    [new VanSalesLineMoney { Currency = "USD", LineCount = 4, Gross = 400m }],
                QuantitiesByUoM =
                    [new VanSalesQuantity { UoMCode = null, Quantity = 120m, LineCount = 10 }],
                CostCurrency = "USD",
                MarginByCurrency =
                    [new VanMarginMoney { Currency = "USD", LineCount = 4, Revenue = 400m, Cost = 240m }]
            },
            Items =
            [
                new VanMarginItem
                {
                    ItemCode = "CHE011",
                    ItemDescription = "Gouda 1kg",
                    LineCount = 6,
                    PostedLineCount = 3,
                    VanCount = 2,
                    RevenueByCurrency = [new VanSalesLineMoney { Currency = "USD", LineCount = 6, Gross = 700m }],
                    CostableRevenueByCurrency =
                        [new VanSalesLineMoney { Currency = "USD", LineCount = 3, Gross = 350m }],
                    QuantitiesByUoM = [new VanSalesQuantity { Quantity = 70m, LineCount = 6 }],
                    UnitCost = 8.25m,
                    CostCurrency = "USD",
                    MarginByCurrency =
                        [new VanMarginMoney { Currency = "USD", LineCount = 3, Revenue = 350m, Cost = 210m }]
                },
                new VanMarginItem
                {
                    ItemCode = "NRI049",
                    ItemDescription = null,
                    LineCount = 4,
                    PostedLineCount = 1,
                    VanCount = 1,
                    RevenueByCurrency = [new VanSalesLineMoney { Currency = "USD", LineCount = 4, Gross = 300m }],
                    CostableRevenueByCurrency =
                        [new VanSalesLineMoney { Currency = "USD", LineCount = 1, Gross = 50m }],
                    QuantitiesByUoM = [new VanSalesQuantity { Quantity = 50m, LineCount = 4 }],
                    // No cost found: the row that proves an absence renders as one.
                    UnitCost = null,
                    CostCurrency = "USD",
                    MarginByCurrency = []
                }
            ],
            Vans =
            [
                new VanMarginVan
                {
                    WarehouseCode = "VAN010",
                    Username = "van010",
                    FullName = "Tendai Moyo",
                    ItemCount = 2,
                    LineCount = 6,
                    PostedLineCount = 3,
                    RevenueByCurrency = [new VanSalesLineMoney { Currency = "USD", LineCount = 6, Gross = 620m }],
                    CostableRevenueByCurrency =
                        [new VanSalesLineMoney { Currency = "USD", LineCount = 3, Gross = 310m }]
                },
                new VanMarginVan
                {
                    WarehouseCode = "VAN011",
                    Username = "van011",
                    FullName = null,
                    ItemCount = 1,
                    LineCount = 4,
                    PostedLineCount = 1,
                    RevenueByCurrency = [new VanSalesLineMoney { Currency = "USD", LineCount = 4, Gross = 380m }],
                    CostableRevenueByCurrency =
                        [new VanSalesLineMoney { Currency = "USD", LineCount = 1, Gross = 90m }]
                }
            ],
            Quality = new VanMarginQuality
            {
                LineCount = 10,
                PostedLineCount = 4,
                ItemsWithNoDescription = 1,
                ItemsWithoutCost = 1,
                ItemCount = 2,
                CostCurrency = "USD",
                CostAttempted = true,
                CurrenciesWithoutMatchingCost = ["ZWG"],
                PostingJobEnabled = false
            }
        };
}
