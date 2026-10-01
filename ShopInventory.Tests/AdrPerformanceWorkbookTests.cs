using ClosedXML.Excel;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Opens the ADR performance workbook and reads its cells back, since the export takes the report
/// and returns bytes and opening them is the only way to know they are right.
/// </summary>
public class AdrPerformanceWorkbookTests
{
    private static readonly Guid Adr = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly ReportExportService _service = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static string TextOf(IXLWorksheet sheet) =>
        string.Join("\n", sheet.CellsUsed().Select(cell => cell.GetFormattedString()));

    [Fact]
    public void Every_ADR_gets_the_contribution_and_the_league()
    {
        using var workbook = Open(_service.ExportAdrPerformanceToExcel(Report(withDetail: false)));

        Assert.Equal(["Contribution", "ADRs"], workbook.Worksheets.Select(sheet => sheet.Name).ToArray());

        var contribution = TextOf(workbook.Worksheet("Contribution"));
        Assert.Contains("WHO RAISED THE ORDER BOOK", contribution);
        Assert.Contains("Merchandisers", contribution);
        // Built with the export's own format rather than written out: "P0" is culture-bound, and the
        // CI runner writes "30 %" where a desktop writes "30%".
        Assert.Contains($"{0.3.ToString("P0")} of USD", contribution);
    }

    [Fact]
    public void One_ADR_adds_their_shops_items_and_orders()
    {
        using var workbook = Open(_service.ExportAdrPerformanceToExcel(Report(withDetail: true)));

        Assert.Equal(
            ["Contribution", "ADRs", "Shops", "Items", "Orders"],
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());

        Assert.Contains("TENDAI NCUBE — SHOPS", TextOf(workbook.Worksheet("Shops")));

        var orders = workbook.Worksheet("Orders");
        var header = orders.CellsUsed().First(cell => cell.GetString() == "Value").Address;
        var value = orders.Cell(header.RowNumber + 1, header.ColumnNumber);

        // The register is the one place money is a number, because each row names its currency.
        Assert.Equal(XLDataType.Number, value.DataType);
        Assert.Equal(120m, value.GetValue<decimal>());
    }

    /// <summary>The caveats reach a forwarded workbook before any figure does.</summary>
    [Fact]
    public void The_caveats_are_written_above_the_tables()
    {
        using var workbook = Open(_service.ExportAdrPerformanceToExcel(Report(withDetail: false)));
        var sheet = workbook.Worksheet("Contribution");

        var caveat = sheet.CellsUsed().First(cell => cell.GetString().StartsWith("2 orders reached SAP")).Address;
        var table = sheet.CellsUsed().First(cell => cell.GetString() == "ADR CONTRIBUTION TO ALL VANS").Address;

        Assert.True(caveat.RowNumber < table.RowNumber);
    }

    [Fact]
    public void An_ADR_with_no_visits_has_no_strike_rate_rather_than_zero()
    {
        using var workbook = Open(_service.ExportAdrPerformanceToExcel(Report(withDetail: false)));
        var sheet = workbook.Worksheet("ADRs");

        var header = sheet.CellsUsed().First(cell => cell.GetString() == "Strike Rate").Address;

        Assert.Equal(0.5.ToString("P0"), sheet.Cell(header.RowNumber + 1, header.ColumnNumber).GetFormattedString());
        Assert.Equal("—", sheet.Cell(header.RowNumber + 2, header.ColumnNumber).GetFormattedString());
    }

    private static AdrPerformanceReportResponse Report(bool withDetail)
    {
        var usd = new VanSalesMoney { Currency = "USD", DocumentCount = 12, DropCount = 10, Gross = 1200m };

        return new AdrPerformanceReportResponse
        {
            FromDate = new DateTime(2026, 9, 1),
            ToDate = new DateTime(2026, 9, 30),
            Overall = new AdrPerformanceOverall
            {
                AdrCount = 2,
                ActiveAdrCount = 2,
                Calls = 20,
                ProductiveCalls = 12,
                Orders = [new AdrContribution { Currency = "USD", AdrDocumentCount = 14, AdrGross = 1300m, VanDocumentCount = 30, VanGross = 3000m }]
            },
            Adrs =
            [
                new AdrPerformanceRow { UserId = Adr, Username = "adr01", FullName = "Tendai Ncube", IsActive = true, Calls = 20, ProductiveCalls = 10, OrderTotalsByCurrency = [usd] },
                new AdrPerformanceRow { UserId = Guid.NewGuid(), Username = "adr02", FullName = "Nyasha Dube", IsActive = true, Calls = null, ProductiveCalls = 2 }
            ],
            OrdersByChannel =
            [
                new AdrChannel { Channel = "Van sales reps", OrderCount = 30, PeopleCount = 4, Shares = [new AdrChannelShare { Currency = "USD", Gross = 2800m, Share = 0.55 }] },
                new AdrChannel { Channel = "ADRs", IsAdr = true, OrderCount = 14, PeopleCount = 2, Shares = [new AdrChannelShare { Currency = "USD", Gross = 1300m, Share = 0.3 }] },
                new AdrChannel { Channel = "Merchandisers", OrderCount = 6, PeopleCount = 1, Shares = [new AdrChannelShare { Currency = "USD", Gross = 650m, Share = 0.15 }] }
            ],
            Detail = withDetail
                ? new AdrPerformanceDetail
                {
                    UserId = Adr,
                    Shops = [new AdrShop { CustomerCode = "SHOP1", CustomerName = "Shop one", OrderCount = 3, OrderTotalsByCurrency = [usd], LastActiveOn = new DateTime(2026, 9, 30) }],
                    Items = [new AdrItem { Rank = 1, ItemCode = "CHE011", LineCount = 5, ShopCount = 3 }],
                    Orders =
                    [
                        new AdrOrder
                        {
                            Id = 41, OrderNumber = "SO-0041", SapDocNum = 5001, TradingDate = new DateTime(2026, 9, 30),
                            CustomerCode = "SHOP1", Status = "Approved", Stage = "In SAP", Currency = "USD", DocTotal = 120m, LineCount = 3
                        }
                    ]
                }
                : null,
            Caveats = ["2 orders reached SAP but are not marked fulfilled here."]
        };
    }
}
