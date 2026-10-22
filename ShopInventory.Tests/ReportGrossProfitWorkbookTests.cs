using ClosedXML.Excel;
using ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Opens the SAP report workbooks and reads their gross profit back: SAP's booked profit in USD and its
/// margin, as the pages show them. The export returns bytes, so opening them is the only way to know.
/// </summary>
public class ReportGrossProfitWorkbookTests
{
    private readonly ReportExportService _service = new();

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    /// <summary>The cell under <paramref name="header"/> on the row whose first cell is <paramref name="rowKey"/>.</summary>
    private static IXLCell Under(IXLWorksheet sheet, string header, string rowKey)
    {
        // The nearest heading above the row: a KPI card above the table can carry the same label.
        var row = sheet.CellsUsed().First(cell => cell.Address.ColumnNumber == 1 && cell.GetString() == rowKey);
        var heading = sheet.CellsUsed()
            .Where(cell => cell.GetString() == header && cell.Address.RowNumber < row.Address.RowNumber)
            .MaxBy(cell => cell.Address.RowNumber)!;
        return sheet.Cell(row.Address.RowNumber, heading.Address.ColumnNumber);
    }

    /// <summary>
    /// The statement deducts SAP's cost of sales from sales before VAT. It used to deduct the period's
    /// purchase orders from the takings, which is stock bought, not stock sold.
    /// </summary>
    [Fact]
    public void Profit_statement_deducts_cost_of_sales_and_shows_purchase_orders_apart()
    {
        using var workbook = Open(_service.ExportProfitOverviewToExcel(new ProfitOverviewReport
        {
            FromDate = new DateTime(2026, 1, 1),
            ToDate = new DateTime(2026, 2, 28),
            TotalRevenueUSD = 1150m,
            NetRevenueUSD = 1150m,
            TotalPurchaseCostUSD = 5000m,
            NetSalesBeforeVatUSD = 900m,
            CostOfSalesUSD = 675m,
            GrossProfitUSD = 225m,
            GrossMarginPercent = 25m,
            MonthlyBreakdown =
            [
                new MonthlyProfit { Month = "Jan 2026", RevenueUSD = 690m, PurchaseCostUSD = 5000m, NetSalesBeforeVatUSD = 600m, GrossProfitUSD = 150m, GrossMarginPercent = 25m },
                new MonthlyProfit { Month = "Feb 2026", RevenueUSD = 460m, NetSalesBeforeVatUSD = 300m, GrossProfitUSD = 75m, GrossMarginPercent = 25m }
            ]
        }));

        var statement = workbook.Worksheet("Profit & Loss");
        Assert.Equal(900m, Under(statement, "USD", "Net Sales before VAT").GetValue<decimal>());
        Assert.Equal(675m, Under(statement, "USD", "Less: Cost of Sales").GetValue<decimal>());
        Assert.Equal(225m, Under(statement, "USD", "GROSS PROFIT").GetValue<decimal>());
        Assert.Equal(5000m, Under(statement, "USD", "Purchase Orders Raised").GetValue<decimal>());
        Assert.DoesNotContain(statement.CellsUsed(), cell => cell.GetString().Contains("Purchases (COGS)"));

        var monthly = workbook.Worksheet("Monthly Breakdown");
        Assert.Equal(450m, Under(monthly, "Cost of Sales", "Jan 2026").GetValue<decimal>());
        Assert.Equal(150m, Under(monthly, "Gross Profit", "Jan 2026").GetValue<decimal>());
    }

    [Fact]
    public void Sales_products_and_customers_workbooks_carry_gross_profit_and_margin()
    {
        using var sales = Open(_service.ExportSalesSummaryToExcel(new SalesSummaryReport
        {
            FromDate = new DateTime(2026, 1, 1),
            ToDate = new DateTime(2026, 1, 31),
            GrossProfitUSD = 40m,
            RevenueBeforeVatUSD = 300m,
            GrossMarginPercent = 13.3m,
            DailySales =
            [
                new DailySales { Date = new DateTime(2026, 1, 15), InvoiceCount = 2, TotalSalesUSD = 230m, GrossProfitUSD = 50m, GrossMarginPercent = 25m },
                new DailySales { Date = new DateTime(2026, 1, 16), InvoiceCount = 1, TotalSalesUSD = 115m, GrossProfitUSD = -10m, GrossMarginPercent = -10m }
            ]
        }));
        var daily = sales.Worksheet("Daily Sales");
        var gpColumn = daily.CellsUsed().First(cell => cell.GetString() == "Gross Profit (USD)").Address.ColumnNumber;
        var dailyProfit = daily.Column(gpColumn).CellsUsed().Where(cell => cell.DataType == XLDataType.Number).Select(cell => cell.GetValue<decimal>()).ToList();
        Assert.Contains(50m, dailyProfit);
        Assert.Contains(-10m, dailyProfit);

        using var products = Open(_service.ExportTopProductsToExcel(new TopProductsReport
        {
            TopProducts =
            [
                new TopProduct { Rank = 1, ItemCode = "SPLIT", ItemName = "Split", TotalQuantitySold = 20m, TotalRevenueUSD = 100m, GrossProfitUSD = 100m, GrossMarginPercent = 25m },
                new TopProduct { Rank = 2, ItemCode = "LOSS", ItemName = "Below cost", TotalQuantitySold = 5m, TotalRevenueUSD = 50m, GrossProfitUSD = -5m, GrossMarginPercent = -10m }
            ]
        }));
        var top = products.Worksheet("Top Products");
        Assert.Equal(100m, Under(top, "Gross Profit (USD)", "1").GetValue<decimal>());
        Assert.Equal(-5m, Under(top, "Gross Profit (USD)", "2").GetValue<decimal>());

        using var customers = Open(_service.ExportTopCustomersToExcel(new TopCustomersReport
        {
            TopCustomers =
            [
                new TopCustomer { Rank = 1, CardCode = "C1", CardName = "Acme", TotalPurchasesUSD = 460m, GrossProfitUSD = 100m, GrossMarginPercent = 25m },
                new TopCustomer { Rank = 2, CardCode = "C2", CardName = "Beta", TotalPurchasesUSD = 50m }
            ]
        }));
        var ranked = customers.Worksheet("Top Customers");
        Assert.Equal(100m, Under(ranked, "Gross Profit (USD)", "1").GetValue<decimal>());
        // No profit row for the customer is shown as none, not as a zero profit.
        Assert.NotEqual(XLDataType.Number, Under(ranked, "Gross Profit (USD)", "2").DataType);
    }

    [Fact]
    public void Item_volume_workbook_carries_net_gross_profit_and_margin()
    {
        var item = new ItemVolumeSalesItemResult { ItemCode = "YOG143", ItemName = "Yoghurt", NetRevenueUsd = 225m, NetGrossProfitUsd = 90m };
        var account = new ItemVolumeSalesAccountResult { CardCode = "CIS006", CardName = "Farm Counter", NetRevenueUsd = 225m, NetGrossProfitUsd = 90m };

        using var workbook = Open(_service.ExportItemVolumeSalesReportToExcel(new GetItemVolumeSalesReportResult
        {
            FromDateUtc = new DateTime(2026, 7, 1),
            ToDateUtc = new DateTime(2026, 7, 31),
            Summary = new ItemVolumeSalesSummaryResult { NetRevenueUsd = 225m, NetGrossProfitUsd = 90m },
            ItemTotals = [item],
            AccountTotals = [account],
            Periods = [new ItemVolumeSalesPeriodResult { PeriodStartUtc = new DateTime(2026, 7, 1), PeriodEndUtc = new DateTime(2026, 7, 31), Label = "Jul 2026", NetRevenueUsd = 225m, NetGrossProfitUsd = 90m }]
        }, "Item volume"));

        var items = workbook.Worksheet("Items");
        Assert.Equal(90m, Under(items, "Net Gross Profit USD", "YOG143").GetValue<decimal>());
        Assert.Equal(0.4m, Under(items, "Margin", "YOG143").GetValue<decimal>());

        var partners = workbook.Worksheet("Business Partners");
        Assert.Equal(90m, Under(partners, "Net Gross Profit USD", "CIS006").GetValue<decimal>());

        var periods = workbook.Worksheet("Periods");
        Assert.Equal(90m, Under(periods, "Net Gross Profit USD", "Jul 2026").GetValue<decimal>());
    }

    [Fact]
    public void Partner_performance_workbook_carries_each_years_gross_profit_and_the_windows_margin()
    {
        GetItemVolumeSalesReportResult Window(int year, decimal revenue, decimal profit) => new()
        {
            FromDateUtc = new DateTime(year, 7, 1),
            ToDateUtc = new DateTime(year, 7, 31),
            Grouping = ItemVolumeSalesGrouping.Monthly,
            AccountTotals = [new ItemVolumeSalesAccountResult { CardCode = "CIS006", CardName = "Farm Counter", NetRevenueUsd = revenue, NetGrossProfitUsd = profit, InvoiceCount = 1 }]
        };

        var performance = PartnerPerformance.Build([Window(2026, 225m, 90m), Window(2025, 200m, 50m)]);

        using var workbook = Open(_service.ExportPartnerPerformanceToExcel(performance, "Partner performance"));

        var partners = workbook.Worksheet("Partners");
        Assert.Equal(90m, Under(partners, "Gross Profit USD 2026", "CIS006").GetValue<decimal>());
        Assert.Equal(50m, Under(partners, "Gross Profit USD 2025", "CIS006").GetValue<decimal>());
        Assert.Equal(0.4m, Under(partners, "Margin 2026", "CIS006").GetValue<decimal>());

        // The periods sheet keeps its own width: its rows carry no gross profit.
        Assert.DoesNotContain(workbook.Worksheet("Periods").CellsUsed(), cell => cell.GetString().StartsWith("Gross Profit"));
    }
}
