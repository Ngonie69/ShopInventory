using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The ADR report's order-to-sale figures across the wire and into the workbook: the API's records
/// read back as the portal's hand-mirrored DTOs, then exported and opened again, because the export
/// takes the report and returns bytes and opening them is the only way to know they are right.
/// </summary>
public class AdrConversionExportTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private static readonly Guid Adr = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OtherAdr = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly ReportExportService _service = new();

    [Fact]
    public void The_order_to_sale_figures_cross_the_wire_intact()
    {
        var conversion = Assert.IsType<AdrConversionReport>(RoundTrip(Populated()).Conversion);

        var money = Assert.Single(conversion.Overall.Money);
        Assert.Equal(1000m, money.Ordered);
        Assert.Equal(800m, money.Invoiced);
        Assert.Equal(0.8, money.Conversion!.Value, 3);
        Assert.Equal(1 - 120.0 / 900, money.FillRate!.Value, 3);
        Assert.Equal(0.4, money.VanShare!.Value, 3);
        Assert.Equal(21.5m, money.MarginPercent);
        Assert.Equal(200m, money.Gap);
        Assert.Equal(0.9, conversion.Overall.MedianDaysToInvoice);
        Assert.Equal(0.5, conversion.Overall.StrikeRate);

        var item = Assert.Single(conversion.Items);
        Assert.Equal(0.75, item.FillRate!.Value, 3);

        var ledger = conversion.Orders;
        Assert.Equal(-30m, ledger[0].Variance);
        Assert.Null(ledger[1].Variance);
        Assert.Equal("No shop recorded", ledger[1].DisplayCustomer);

        var previous = Assert.IsType<AdrConversionComparison>(conversion.Previous);
        Assert.Equal(0.7, Assert.Single(previous.Overall.Money).Conversion!.Value, 3);
    }

    /// <summary>Every nullable the API can send as null, at once.</summary>
    [Fact]
    public void Every_null_the_API_can_send_is_read_as_null()
    {
        var empty = new AdrConversionSummaryResult(
            new AdrFunnelCountsResult(0, 0, 0, 0, 0, 0, 0, 0, 0), [], null, null, null, 0);
        var result = Populated();
        var report = new AdrPerformanceReportResult(
            result.FromDate, result.ToDate, result.Overall, result.Adrs, result.OrdersByChannel, null, [])
        {
            Conversion = new AdrConversionReportResult(
                empty,
                [new AdrConversionRowResult(Adr, null, null, empty)],
                [new AdrConversionDayResult(Adr, new DateTime(2026, 9, 30), null, 0, 0, [])],
                [new AdrConversionShopResult(Adr, "SHOP9", null, 2, 0, 0, 0, 0, [], null)],
                [new AdrConversionItemResult(Adr, "CHE011", null, null, 1, 1, 0m, 0m, 0m, 0, [])],
                [new AdrLedgerOrderResult(1, "SO-1", Adr, new DateTime(2026, 9, 30), new DateTime(2026, 9, 30, 10, 0, 0),
                    null, null, "USD", 0, 0m, null, AdrOutcomes.NotYetInSap, null, null, null, null, null, 0m, 0m)],
                null)
        };

        var conversion = Assert.IsType<AdrConversionReport>(RoundTrip(report).Conversion);

        Assert.Null(conversion.Overall.MedianDaysToInvoice);
        Assert.Null(conversion.Overall.Calls);
        Assert.Null(conversion.Overall.StrikeRate);
        Assert.Null(conversion.Previous);
        Assert.Null(Assert.Single(conversion.Days).Calls);
        Assert.Null(Assert.Single(conversion.Shops).LastOrderOn);
        Assert.Equal("Visited, never ordered", AdrConversionReading.ShopFlag(conversion.Shops[0], "USD"));
        Assert.Null(Assert.Single(conversion.Items).FillRate);
        Assert.Null(Assert.Single(conversion.Orders).Invoiced);

        // A workbook of nothing still opens, with every sheet saying so rather than breaking.
        using var workbook = Open(_service.ExportAdrPerformanceToExcel(RoundTrip(report)));
        Assert.Equal(8, workbook.Worksheets.Count);
    }

    [Fact]
    public void The_workbook_has_eight_sheets_opening_on_the_summary()
    {
        using var workbook = Open(Export());

        Assert.Equal(
            ["Summary", "ADR League", "Daily", "Gap by ADR", "Shops", "Items Fill", "Order Ledger", "Notes"],
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
        Assert.True(workbook.Worksheet("Summary").TabActive);

        var summary = TextOf(workbook.Worksheet("Summary"));
        Assert.Contains("ORDER TO SALE", summary);
        Assert.Contains("WHERE THE GAP WENT", summary);
        Assert.Contains("NEEDS ATTENTION (USD)", summary);
        Assert.Contains("WHO RAISED THE ORDER BOOK", summary);
    }

    /// <summary>The caveats reach a forwarded workbook before any figure does.</summary>
    [Fact]
    public void The_caveats_are_written_above_the_figures()
    {
        using var workbook = Open(Export());
        var sheet = workbook.Worksheet("Summary");

        var caveat = sheet.CellsUsed().First(cell => cell.GetString().Contains("captured without prices"));
        var headline = sheet.CellsUsed().First(cell => cell.GetString().StartsWith("HEADLINE", StringComparison.Ordinal));

        Assert.True(caveat.Address.RowNumber < headline.Address.RowNumber);
    }

    /// <summary>Money is a number in a column of its own currency, so it can be filtered and summed.</summary>
    [Fact]
    public void League_money_is_a_number_and_the_rates_are_formulas()
    {
        using var workbook = Open(Export());
        var league = workbook.Worksheet("ADR League");

        var header = league.CellsUsed().First(cell => cell.GetString() == "USD Invoiced").Address;
        var first = league.Cell(header.RowNumber + 1, header.ColumnNumber);

        Assert.Equal(XLDataType.Number, first.DataType);
        Assert.Equal(500m, first.GetValue<decimal>());
        Assert.Equal("Tendai Ncube", league.Cell(header.RowNumber + 1, 2).GetString());

        var conversion = league.Cell(header.RowNumber + 1, header.ColumnNumber + 1);
        Assert.True(conversion.HasFormula);

        var totals = league.Cell(header.RowNumber + 3, header.ColumnNumber);
        Assert.StartsWith("SUBTOTAL(109", totals.FormulaA1);
        Assert.NotNull(league.AutoFilter);
    }

    [Fact]
    public void The_ledger_keeps_a_cancelled_order_struck_through()
    {
        using var workbook = Open(Export());
        var ledger = workbook.Worksheet("Order Ledger");

        var cancelled = ledger.CellsUsed().First(cell => cell.GetString() == "SO-0003");
        Assert.True(cancelled.Style.Font.Strikethrough);

        var invoiced = ledger.CellsUsed().First(cell => cell.GetString() == "SO-0001");
        var variance = ledger.Row(invoiced.Address.RowNumber).Cell(18);
        Assert.Equal("N" + invoiced.Address.RowNumber + "-I" + invoiced.Address.RowNumber, variance.FormulaA1);
    }

    /// <summary>
    /// The charts are added by reopening the bytes with the OpenXML SDK, which is where a misordered
    /// drawing makes Excel offer to repair the file and drop them. Validated, with a negative control so
    /// "no errors" is known to mean the validator looked.
    /// </summary>
    [Fact]
    public void The_package_is_valid_and_carries_both_charts()
    {
        var bytes = Export();

        using (var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false))
        {
            var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).ToList();
            Assert.Empty(errors.Select(error => $"{error.Path?.XPath}: {error.Description}"));

            var summary = document.WorkbookPart!.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
                .First(sheet => sheet.Name == "Summary");
            var part = (WorksheetPart)document.WorkbookPart.GetPartById(summary.Id!);
            Assert.Equal(2, part.DrawingsPart!.ChartParts.Count());
        }

        // The negative control: append the drawing at the end, where ClosedXML's tableParts makes it
        // schema-invalid, and the validator must say so.
        using var copy = new MemoryStream();
        copy.Write(bytes);
        using (var document = SpreadsheetDocument.Open(copy, true))
        {
            var summary = document.WorkbookPart!.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
                .First(sheet => sheet.Name == "Summary");
            var part = (WorksheetPart)document.WorkbookPart.GetPartById(summary.Id!);
            var drawing = part.Worksheet.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.Drawing>()!;
            drawing.Remove();
            part.Worksheet.Append(drawing);

            Assert.NotEmpty(new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document));
        }
    }

    // ── Building ────────────────────────────────────────────────────────────────

    private static AdrPerformanceReportResponse RoundTrip(AdrPerformanceReportResult result)
    {
        var json = JsonSerializer.Serialize(result, Wire);
        var mirrored = JsonSerializer.Deserialize<AdrPerformanceReportResponse>(json, Wire);
        Assert.NotNull(mirrored);
        return mirrored;
    }

    private byte[] Export() => _service.ExportAdrPerformanceToExcel(RoundTrip(Populated()));

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextOf(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed().Select(cell => cell.GetFormattedString()));

    private static AdrConversionMoneyResult Money(decimal ordered, decimal invoiced, decimal shortSupplied, decimal orderedOnInvoiced,
        decimal vanSales, decimal adrLed) =>
        new("USD", ordered, ordered - 40m, invoiced, shortSupplied, ordered - invoiced - shortSupplied - 40m, 40m, 0m, 0m,
            orderedOnInvoiced, vanSales, adrLed)
        {
            GrossProfit = 172m,
            CostedNetAmount = 800m
        };

    private static AdrConversionSummaryResult Summary(AdrConversionMoneyResult money, int orders, double? median) =>
        new(new AdrFunnelCountsResult(orders, orders - 1, orders - 2, orders - 3, 1, 0, 1, 0, 0), [money], median, 0.75, 20, 10);

    private static AdrPerformanceReportResult Populated()
    {
        var usd = new VanSalesMoneyResult("USD", 3, 3, 1000m);
        var overall = Summary(Money(1000m, 800m, 120m, 900m, 2000m, 800m), 5, 0.9);
        var tendai = Summary(Money(600m, 500m, 60m, 560m, 1200m, 500m), 3, 0.5);
        var other = Summary(Money(400m, 300m, 60m, 340m, 800m, 300m) , 2, 1.4);

        return new AdrPerformanceReportResult(
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 30),
            new AdrPerformanceOverallResult(2, 2, new AdrOrderCountsResult(5, 4, 3, 1, 1),
                [new AdrContributionResult("USD", 5, 1000m, 20, 4000m)], [], 20, 10),
            [
                new AdrPerformanceRepResult(Adr, "adr01", "Tendai Ncube", "VAN015", true, 6,
                    new AdrOrderCountsResult(3, 3, 2, 0, 0), 2, [usd], 0, [], [], 12, 6),
                new AdrPerformanceRepResult(OtherAdr, "adr02", "Rumbi Moyo", "VAN016", true, 4,
                    new AdrOrderCountsResult(2, 1, 1, 0, 1), 1, [], 0, [], [], 8, 4)
            ],
            [new AdrChannelResult("ADRs", true, 5, 2, [new AdrChannelShareResult("USD", 1000m, 0.25)])],
            null,
            ["2 orders were captured without prices and priced when invoiced."])
        {
            Conversion = new AdrConversionReportResult(
                overall,
                [
                    new AdrConversionRowResult(Adr, "R11", "Epworth", tendai),
                    new AdrConversionRowResult(OtherAdr, null, null, other)
                ],
                [
                    new AdrConversionDayResult(Adr, new DateTime(2026, 9, 29), 6, 3, 2,
                        [new AdrDayMoneyResult("USD", 400m, 340m, 40m)]),
                    new AdrConversionDayResult(OtherAdr, new DateTime(2026, 9, 30), 8, 4, 3,
                        [new AdrDayMoneyResult("USD", 600m, 460m, 80m)])
                ],
                [
                    new AdrConversionShopResult(Adr, "SHOP1", "Shop one", 4, 3, 2, 2, 1,
                        [new AdrDayMoneyResult("USD", 600m, 500m, 60m)], new DateTime(2026, 9, 29))
                ],
                [
                    new AdrConversionItemResult(Adr, "CHE011", "Cheddar 1kg", "EA", 3, 2, 20m, 15m, 5m, 1,
                        [new AdrItemMoneyResult("USD", 400m, 120m)])
                ],
                [
                    new AdrLedgerOrderResult(1, "SO-0001", Adr, new DateTime(2026, 9, 29), new DateTime(2026, 9, 29, 9, 30, 0),
                        "SHOP1", "Shop one", "USD", 3, 230m, 7001, AdrOutcomes.ShortSupplied, "VO-1", 418001,
                        new DateTime(2026, 9, 29, 15, 0, 0), 200m, 0.23, 30m, 0m),
                    new AdrLedgerOrderResult(2, "SO-0002", OtherAdr, new DateTime(2026, 9, 30), new DateTime(2026, 9, 30, 9, 0, 0),
                        null, null, "USD", 2, 40m, 7002, AdrOutcomes.NoLinkedInvoice, null, null, null, null, null, 0m, 0m),
                    new AdrLedgerOrderResult(3, "SO-0003", OtherAdr, new DateTime(2026, 9, 30), new DateTime(2026, 9, 30, 11, 0, 0),
                        "SHOP9", "Shop nine", "USD", 1, 20m, null, AdrOutcomes.Cancelled, null, null, null, null, null, 0m, 0m)
                ],
                new AdrConversionComparisonResult(
                    new DateTime(2026, 8, 1),
                    new DateTime(2026, 8, 30),
                    Summary(Money(900m, 630m, 100m, 800m, 1800m, 630m), 4, 1.1),
                    []))
        };
    }
}
