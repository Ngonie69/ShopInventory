using ClosedXML.Excel;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the till takings workbook: that it opens, carries one sheet per breakdown, and states each
/// payment method's figures where a reader looks for them.
/// </summary>
/// <remarks>
/// Read back through ClosedXML rather than checked for "some bytes came out", because the export is
/// what an accountant reconciles the drawer against and a column one to the left is a wrong answer
/// that still downloads.
/// </remarks>
public sealed class DesktopSalesAnalysisExportTests
{
    [Fact]
    public void The_workbook_has_a_sheet_for_every_breakdown()
    {
        using var workbook = Export(Report());

        Assert.Equal(
            new[] { "Payment Methods", "By Day", "By Shop", "By Operator", "By Source", "By Hour", "Best Sellers" },
            workbook.Worksheets.Select(sheet => sheet.Name));
    }

    [Fact]
    public void Each_payment_method_is_a_row_with_its_takings_and_share()
    {
        using var workbook = Export(Report());
        var sheet = workbook.Worksheet("Payment Methods");

        var ecocash = RowWhere(sheet, column: 2, "EcoCash");

        Assert.Equal("USD", sheet.Cell(ecocash, 1).GetString());
        Assert.Equal(2, sheet.Cell(ecocash, 3).GetValue<int>());
        Assert.Equal(66m, sheet.Cell(ecocash, 5).GetValue<decimal>());
        Assert.Equal(0.214m, sheet.Cell(ecocash, 6).GetValue<decimal>());

        // A wallet sale with no reference is the one number here somebody has to chase.
        Assert.Equal(1, sheet.Cell(ecocash, 9).GetValue<int>());

        // Not stated for cash, where every sale lacks a reference and the count would only alarm.
        Assert.True(sheet.Cell(RowWhere(sheet, column: 2, "Cash"), 9).IsEmpty());
    }

    [Fact]
    public void The_day_sheet_splits_each_day_by_payment_method_in_its_own_column()
    {
        using var workbook = Export(Report());
        var sheet = workbook.Worksheet("By Day");

        var header = RowWhere(sheet, column: 1, "Currency");
        var ecocashColumn = ColumnWhere(sheet, header, "EcoCash");
        var takingsColumn = ColumnWhere(sheet, header, "Takings");

        Assert.Equal(66m, sheet.Cell(header + 1, ecocashColumn).GetValue<decimal>());
        Assert.Equal(308.70m, sheet.Cell(header + 1, takingsColumn).GetValue<decimal>());
    }

    // ---- Harness ----------------------------------------------------------------------------------

    private static XLWorkbook Export(DesktopSalesAnalysisResult report) =>
        new(new MemoryStream(new ReportExportService().ExportDesktopSalesAnalysisToExcel(report)));

    private static int RowWhere(IXLWorksheet sheet, int column, string value) =>
        sheet.RowsUsed().First(row => row.Cell(column).GetString() == value).RowNumber();

    private static int ColumnWhere(IXLWorksheet sheet, int row, string value) =>
        sheet.Row(row).CellsUsed().First(cell => cell.GetString() == value).Address.ColumnNumber;

    private static DesktopSalesAnalysisResult Report()
    {
        string[] methods = ["Cash", "Swipe", "Ecocash", "Not recorded"];

        List<DesktopSalesPaymentAmount> Split(decimal cash, decimal swipe, decimal ecocash, decimal none) =>
        [
            new() { PaymentMethod = "Cash", TotalAmount = cash },
            new() { PaymentMethod = "Swipe", TotalAmount = swipe },
            new() { PaymentMethod = "Ecocash", TotalAmount = ecocash },
            new() { PaymentMethod = "Not recorded", TotalAmount = none },
        ];

        return new DesktopSalesAnalysisResult
        {
            FromDate = new DateTime(2026, 9, 5),
            ToDate = new DateTime(2026, 9, 11),
            GeneratedAtUtc = new DateTime(2026, 9, 11, 6, 0, 0, DateTimeKind.Utc),
            PaymentMethods = [.. methods],
            Currencies =
            [
                new DesktopSalesCurrencyAnalysis
                {
                    Currency = "USD",
                    SalesCount = 11,
                    TotalAmount = 308.70m,
                    VatAmount = 41.43m,
                    NetAmount = 267.27m,
                    AmountPaid = 327.00m,
                    ChangeGiven = 18.30m,
                    AverageSale = 28.06m,
                    DaysTraded = 1,
                    ByPaymentMethod =
                    [
                        new() { PaymentMethod = "Cash", SalesCount = 5, TotalAmount = 102.55m, ShareOfValuePercent = 33.2m, ChangeGiven = 18.30m },
                        new() { PaymentMethod = "Swipe", SalesCount = 3, TotalAmount = 132.15m, ShareOfValuePercent = 42.8m },
                        new() { PaymentMethod = "Ecocash", SalesCount = 2, TotalAmount = 66.00m, ShareOfValuePercent = 21.4m, WithoutReferenceCount = 1 },
                        new() { PaymentMethod = "Not recorded", SalesCount = 1, TotalAmount = 8.00m, ShareOfValuePercent = 2.6m },
                    ],
                    ByDay =
                    [
                        new() { Date = new DateTime(2026, 9, 11), SalesCount = 11, TotalAmount = 308.70m, ByPaymentMethod = Split(102.55m, 132.15m, 66.00m, 8.00m) },
                    ],
                    ByHour = [new() { Hour = 10, SalesCount = 11, TotalAmount = 308.70m }],
                    ByWarehouse =
                    [
                        new() { Key = "KEFSHOP", Label = "KEFSHOP", SalesCount = 11, TotalAmount = 308.70m, ShareOfValuePercent = 100m, ByPaymentMethod = Split(102.55m, 132.15m, 66.00m, 8.00m) },
                    ],
                    TopItems = [new() { ItemCode = "MILK-1L", ItemDescription = "Full cream milk 1L", Quantity = 20, NetAmount = 90m, SalesCount = 4, ShareOfNetPercent = 33.7m }],
                },
            ],
        };
    }
}
