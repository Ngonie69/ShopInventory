using ClosedXML.Excel;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The sheets an ADR performance workbook gains when it is exported with one ADR chosen: their
/// shops, the items they ordered, and their orders. The contribution and league sheets are in
/// <c>ReportExportService.cs</c> beside the other van reports.
/// </summary>
public partial class ReportExportService
{
    private static void BuildAdrShopsSheet(XLWorkbook workbook, AdrPerformanceDetail detail, string adr, DateTime now)
    {
        const int lastCol = 7;
        var ws = workbook.Worksheets.Add("Shops");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"{adr.ToUpperInvariant()} — SHOPS", lastCol, now);
        row = TsColumnHeaders(ws, row, lastCol,
            ["Code", "Shop", "Orders", "Order Value", "Sales", "Sales Value", "Last Active"]);

        int index = 0;
        foreach (var shop in detail.Shops)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = shop.CustomerCode;
            ws.Cell(row, 2).Value = shop.CustomerName ?? "—";
            ws.Cell(row, 3).Value = shop.OrderCount;
            ws.Cell(row, 4).Value = MoneyText(shop.OrderTotalsByCurrency);
            ws.Cell(row, 5).Value = shop.SaleCount;
            ws.Cell(row, 6).Value = MoneyText(shop.SalesTotalsByCurrency);
            WriteVanPerformanceDate(ws.Cell(row, 7), shop.LastActiveOn);
            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 2);
    }

    private static void BuildAdrItemsSheet(XLWorkbook workbook, AdrPerformanceDetail detail, string adr, DateTime now)
    {
        const int lastCol = 7;
        var ws = workbook.Worksheets.Add("Items");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"{adr.ToUpperInvariant()} — ITEMS ORDERED, RANKED ON REACH", lastCol, now);
        row = TsColumnHeaders(ws, row, lastCol,
            ["#", "Item Code", "Description", "Lines", "Shops", "Quantity", "Line Value"]);

        int index = 0;
        foreach (var item in detail.Items)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = item.Rank;
            ws.Cell(row, 2).Value = item.ItemCode;
            ws.Cell(row, 3).Value = item.ItemDescription ?? "—";
            ws.Cell(row, 4).Value = item.LineCount;
            ws.Cell(row, 5).Value = item.ShopCount;
            ws.Cell(row, 6).Value = QuantityText(item.QuantitiesByUoM);
            ws.Cell(row, 7).Value = LineMoneyText(item.TotalsByCurrency);
            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 2);
    }

    /// <summary>
    /// The one sheet where money is a number: every row names its own currency, so a reader can
    /// filter to one before summing.
    /// </summary>
    private static void BuildAdrOrdersSheet(XLWorkbook workbook, AdrPerformanceDetail detail, string adr, DateTime now)
    {
        const int lastCol = 8;
        var ws = workbook.Worksheets.Add("Orders");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"{adr.ToUpperInvariant()} — ORDERS", lastCol, now);

        if (detail.OrdersNotListed > 0)
        {
            ws.Cell(row, 1).Value =
                $"The newest {detail.Orders.Count:N0} orders. {detail.OrdersNotListed:N0} older ones are counted on the other sheets but not listed.";
            ws.Range(row, 1, row, lastCol).Merge();
            ws.Cell(row, 1).Style.Font.Italic = true;
            ws.Cell(row, 1).Style.Font.FontColor = TsOrange;
            row += 2;
        }

        row = TsColumnHeaders(ws, row, lastCol,
            ["Date", "Order", "SAP No.", "Shop", "Stage", "Lines", "Currency", "Value"]);

        int index = 0;
        foreach (var order in detail.Orders)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            WriteVanPerformanceDate(ws.Cell(row, 1), order.TradingDate);
            ws.Cell(row, 2).Value = order.OrderNumber;

            if (order.SapDocNum is { } docNum)
            {
                ws.Cell(row, 3).Value = docNum;
            }
            else
            {
                ws.Cell(row, 3).Value = "—";
            }

            ws.Cell(row, 4).Value = order.DisplayCustomer;
            ws.Cell(row, 5).Value = order.Stage;
            ws.Cell(row, 6).Value = order.LineCount;
            ws.Cell(row, 7).Value = order.Currency;
            ws.Cell(row, 8).Value = order.DocTotal;
            ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";

            if (order.Stage == "Cancelled")
            {
                ws.Range(row, 1, row, lastCol).Style.Font.Strikethrough = true;
                ws.Range(row, 1, row, lastCol).Style.Font.FontColor = TsTextMuted;
            }

            row++;
            index++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2, freezeCol: 2);
    }
}
