using ClosedXML.Excel;
using ShopInventory.Web.Models;
using System.Text;

namespace ShopInventory.Web.Services;

/// <summary>
/// Service for exporting reports to Excel and PDF-ready HTML
/// </summary>
public interface IReportExportService
{
    byte[] ExportSalesSummaryToExcel(SalesSummaryReport report);
    byte[] ExportTopProductsToExcel(TopProductsReport report);
    byte[] ExportStockSummaryToExcel(StockSummaryReport report);
    byte[] ExportPaymentSummaryToExcel(PaymentSummaryReport report);
    byte[] ExportTopCustomersToExcel(TopCustomersReport report);
    byte[] ExportLowStockAlertsToExcel(LowStockAlertReport report);
    byte[] ExportOrderFulfillmentToExcel(OrderFulfillmentReport report);
    byte[] ExportCreditNoteSummaryToExcel(CreditNoteSummaryReport report);
    byte[] ExportPurchaseOrderSummaryToExcel(PurchaseOrderSummaryReport report);
    byte[] ExportReceivablesAgingToExcel(ReceivablesAgingReport report);
    byte[] ExportProfitOverviewToExcel(ProfitOverviewReport report);
    byte[] ExportSlowMovingProductsToExcel(SlowMovingProductsReport report);
    string GeneratePrintableHtml(string title, string content, DateTime? fromDate = null, DateTime? toDate = null);
}

public class ReportExportService : IReportExportService
{
    private static void StyleHeader(IXLWorksheet ws, int lastCol)
    {
        var headerRange = ws.Range(1, 1, 1, lastCol);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1a237e");
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        headerRange.Style.Border.BottomBorderColor = XLColor.FromHtml("#0d47a1");
    }

    private static void StyleDataRows(IXLWorksheet ws, int lastRow, int lastCol)
    {
        if (lastRow < 2) return;
        var dataRange = ws.Range(2, 1, lastRow, lastCol);
        dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        dataRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#e0e0e0");
        dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        dataRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#bdbdbd");

        // Alternate row colors
        for (int r = 2; r <= lastRow; r++)
        {
            if (r % 2 == 0)
                ws.Range(r, 1, r, lastCol).Style.Fill.BackgroundColor = XLColor.FromHtml("#f5f5f5");
        }
    }

    private static void FinalizeSheet(IXLWorksheet ws, int lastCol)
    {
        ws.Columns(1, lastCol).AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    public byte[] ExportSalesSummaryToExcel(SalesSummaryReport report)
    {
        using var workbook = new XLWorkbook();

        // Summary sheet
        var summary = workbook.Worksheets.Add("Summary");
        summary.Cell(1, 1).Value = "Sales Summary Report";
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(1, 1).Style.Font.FontSize = 16;
        summary.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#1a237e");
        summary.Range(1, 1, 1, 4).Merge();

        summary.Cell(2, 1).Value = $"Period: {report.FromDate:dd MMM yyyy} - {report.ToDate:dd MMM yyyy}";
        summary.Range(2, 1, 2, 4).Merge();
        summary.Cell(2, 1).Style.Font.Italic = true;

        var metrics = new[] {
            ("Total Invoices", report.TotalInvoices.ToString()),
            ("Total Sales (USD)", report.TotalSalesUSD.ToString("C2")),
            ("Total Sales (ZIG)", $"ZIG {report.TotalSalesZIG:N2}"),
            ("VAT (USD)", report.TotalVatUSD.ToString("C2")),
            ("VAT (ZIG)", $"ZIG {report.TotalVatZIG:N2}"),
            ("Average Invoice (USD)", report.AverageInvoiceValueUSD.ToString("C2")),
            ("Unique Customers", report.UniqueCustomers.ToString())
        };

        for (int i = 0; i < metrics.Length; i++)
        {
            summary.Cell(4 + i, 1).Value = metrics[i].Item1;
            summary.Cell(4 + i, 1).Style.Font.Bold = true;
            summary.Cell(4 + i, 2).Value = metrics[i].Item2;
        }
        summary.Columns().AdjustToContents();

        // Daily breakdown sheet
        var daily = workbook.Worksheets.Add("Daily Sales");
        daily.Cell(1, 1).Value = "Date";
        daily.Cell(1, 2).Value = "Invoices";
        daily.Cell(1, 3).Value = "Sales (USD)";
        daily.Cell(1, 4).Value = "Sales (ZIG)";
        StyleHeader(daily, 4);

        int row = 2;
        foreach (var day in report.DailySales.OrderByDescending(d => d.Date))
        {
            daily.Cell(row, 1).Value = day.Date.ToString("dd MMM yyyy");
            daily.Cell(row, 2).Value = day.InvoiceCount;
            daily.Cell(row, 3).Value = day.TotalSalesUSD;
            daily.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
            daily.Cell(row, 4).Value = day.TotalSalesZIG;
            daily.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }
        StyleDataRows(daily, row - 1, 4);
        FinalizeSheet(daily, 4);

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportTopProductsToExcel(TopProductsReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Top Products");

        ws.Cell(1, 1).Value = "Rank";
        ws.Cell(1, 2).Value = "Item Code";
        ws.Cell(1, 3).Value = "Item Name";
        ws.Cell(1, 4).Value = "Qty Sold";
        ws.Cell(1, 5).Value = "Times Ordered";
        ws.Cell(1, 6).Value = "Revenue (USD)";
        ws.Cell(1, 7).Value = "Revenue (ZIG)";
        StyleHeader(ws, 7);

        int row = 2;
        foreach (var p in report.TopProducts)
        {
            ws.Cell(row, 1).Value = p.Rank;
            ws.Cell(row, 2).Value = p.ItemCode;
            ws.Cell(row, 3).Value = p.ItemName;
            ws.Cell(row, 4).Value = p.TotalQuantitySold;
            ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 5).Value = p.TimesOrdered;
            ws.Cell(row, 6).Value = p.TotalRevenueUSD;
            ws.Cell(row, 6).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 7).Value = p.TotalRevenueZIG;
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }
        StyleDataRows(ws, row - 1, 7);
        FinalizeSheet(ws, 7);

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportStockSummaryToExcel(StockSummaryReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Stock Summary");

        ws.Cell(1, 1).Value = "Warehouse Code";
        ws.Cell(1, 2).Value = "Warehouse Name";
        ws.Cell(1, 3).Value = "Products";
        ws.Cell(1, 4).Value = "Total Qty";
        StyleHeader(ws, 4);

        int row = 2;
        foreach (var wh in report.StockByWarehouse)
        {
            ws.Cell(row, 1).Value = wh.WarehouseCode;
            ws.Cell(row, 2).Value = wh.WarehouseName;
            ws.Cell(row, 3).Value = wh.ProductCount;
            ws.Cell(row, 4).Value = wh.TotalQuantity;
            ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0";
            row++;
        }
        // Totals row
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 3).Value = report.TotalProducts;
        ws.Cell(row, 3).Style.Font.Bold = true;
        ws.Cell(row, 4).FormulaA1 = $"SUM(D2:D{row - 1})";
        ws.Cell(row, 4).Style.Font.Bold = true;

        StyleDataRows(ws, row, 4);
        FinalizeSheet(ws, 4);

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportPaymentSummaryToExcel(PaymentSummaryReport report)
    {
        using var workbook = new XLWorkbook();

        // Methods sheet
        var methods = workbook.Worksheets.Add("By Method");
        methods.Cell(1, 1).Value = "Payment Method";
        methods.Cell(1, 2).Value = "Count";
        methods.Cell(1, 3).Value = "Amount (USD)";
        methods.Cell(1, 4).Value = "Amount (ZIG)";
        methods.Cell(1, 5).Value = "% of Total";
        StyleHeader(methods, 5);

        int row = 2;
        foreach (var m in report.PaymentsByMethod)
        {
            methods.Cell(row, 1).Value = m.PaymentMethod;
            methods.Cell(row, 2).Value = m.PaymentCount;
            methods.Cell(row, 3).Value = m.TotalAmountUSD;
            methods.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
            methods.Cell(row, 4).Value = m.TotalAmountZIG;
            methods.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
            methods.Cell(row, 5).Value = m.PercentageOfTotal / 100;
            methods.Cell(row, 5).Style.NumberFormat.Format = "0.0%";
            row++;
        }
        StyleDataRows(methods, row - 1, 5);
        FinalizeSheet(methods, 5);

        // Daily sheet
        var daily = workbook.Worksheets.Add("Daily Payments");
        daily.Cell(1, 1).Value = "Date";
        daily.Cell(1, 2).Value = "Count";
        daily.Cell(1, 3).Value = "Amount (USD)";
        daily.Cell(1, 4).Value = "Amount (ZIG)";
        StyleHeader(daily, 4);

        row = 2;
        foreach (var d in report.DailyPayments.OrderByDescending(d => d.Date))
        {
            daily.Cell(row, 1).Value = d.Date.ToString("dd MMM yyyy");
            daily.Cell(row, 2).Value = d.PaymentCount;
            daily.Cell(row, 3).Value = d.TotalAmountUSD;
            daily.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
            daily.Cell(row, 4).Value = d.TotalAmountZIG;
            daily.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }
        StyleDataRows(daily, row - 1, 4);
        FinalizeSheet(daily, 4);

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportTopCustomersToExcel(TopCustomersReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Top Customers");

        ws.Cell(1, 1).Value = "Rank";
        ws.Cell(1, 2).Value = "Code";
        ws.Cell(1, 3).Value = "Customer Name";
        ws.Cell(1, 4).Value = "Invoices";
        ws.Cell(1, 5).Value = "Purchases (USD)";
        ws.Cell(1, 6).Value = "Purchases (ZIG)";
        ws.Cell(1, 7).Value = "Payments (USD)";
        ws.Cell(1, 8).Value = "Balance (USD)";
        StyleHeader(ws, 8);

        int row = 2;
        foreach (var c in report.TopCustomers)
        {
            ws.Cell(row, 1).Value = c.Rank;
            ws.Cell(row, 2).Value = c.CardCode;
            ws.Cell(row, 3).Value = c.CardName;
            ws.Cell(row, 4).Value = c.InvoiceCount;
            ws.Cell(row, 5).Value = c.TotalPurchasesUSD;
            ws.Cell(row, 5).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 6).Value = c.TotalPurchasesZIG;
            ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 7).Value = c.TotalPaymentsUSD;
            ws.Cell(row, 7).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 8).Value = c.OutstandingBalanceUSD;
            ws.Cell(row, 8).Style.NumberFormat.Format = "$#,##0.00";
            if (c.OutstandingBalanceUSD > 0)
                ws.Cell(row, 8).Style.Font.FontColor = XLColor.Red;
            row++;
        }
        StyleDataRows(ws, row - 1, 8);
        FinalizeSheet(ws, 8);

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportLowStockAlertsToExcel(LowStockAlertReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Low Stock Alerts");

        ws.Cell(1, 1).Value = "Alert";
        ws.Cell(1, 2).Value = "Item Code";
        ws.Cell(1, 3).Value = "Item Name";
        ws.Cell(1, 4).Value = "Warehouse";
        ws.Cell(1, 5).Value = "Current Stock";
        ws.Cell(1, 6).Value = "Reorder Level";
        ws.Cell(1, 7).Value = "Suggested Order";
        StyleHeader(ws, 7);

        int row = 2;
        foreach (var item in report.Items.OrderBy(i => i.AlertLevel == "Critical" ? 0 : 1).ThenBy(i => i.CurrentStock))
        {
            ws.Cell(row, 1).Value = item.AlertLevel;
            if (item.AlertLevel == "Critical")
            {
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.White;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.Red;
            }
            else
            {
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.Black;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#fff3cd");
            }
            ws.Cell(row, 2).Value = item.ItemCode;
            ws.Cell(row, 3).Value = item.ItemName;
            ws.Cell(row, 4).Value = item.WarehouseCode;
            ws.Cell(row, 5).Value = item.CurrentStock;
            ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 6).Value = item.ReorderLevel;
            ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 7).Value = item.SuggestedReorderQty;
            ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0";
            row++;
        }
        StyleDataRows(ws, row - 1, 7);
        FinalizeSheet(ws, 7);

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportOrderFulfillmentToExcel(OrderFulfillmentReport report)
    {
        using var workbook = new XLWorkbook();

        // Summary sheet
        var summary = workbook.Worksheets.Add("Summary");
        summary.Cell(1, 1).Value = "Order Fulfillment Summary";
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(1, 1).Style.Font.FontSize = 14;
        summary.Cell(2, 1).Value = $"Period: {report.FromDate:dd MMM yyyy} - {report.ToDate:dd MMM yyyy}";

        summary.Cell(4, 1).Value = "Metric"; summary.Cell(4, 2).Value = "Value";
        var summaryHeader = summary.Range(4, 1, 4, 2);
        summaryHeader.Style.Font.Bold = true;
        summaryHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#1a237e");
        summaryHeader.Style.Font.FontColor = XLColor.White;

        int r = 5;
        void AddMetric(string label, object val) { summary.Cell(r, 1).Value = label; summary.Cell(r, 2).SetValue(val?.ToString() ?? ""); r++; }
        AddMetric("Total Orders", report.TotalOrders);
        AddMetric("Open Orders", report.OpenOrders);
        AddMetric("Closed Orders", report.ClosedOrders);
        AddMetric("Cancelled Orders", report.CancelledOrders);
        AddMetric("Fulfillment Rate", $"{report.FulfillmentRatePercent:N1}%");
        AddMetric("Total Order Value (USD)", $"${report.TotalOrderValueUSD:N2}");
        AddMetric("Total Order Value (ZIG)", $"ZIG {report.TotalOrderValueZIG:N2}");
        AddMetric("Pending Value (USD)", $"${report.TotalPendingValueUSD:N2}");
        AddMetric("Total Line Items", report.TotalLineItems);
        AddMetric("Fully Delivered Lines", report.FullyDeliveredLines);
        AddMetric("Partially Delivered Lines", report.PartiallyDeliveredLines);
        AddMetric("Undelivered Lines", report.UndeliveredLines);
        summary.Columns(1, 2).AdjustToContents();

        // Orders sheet
        var ws = workbook.Worksheets.Add("Orders");
        ws.Cell(1, 1).Value = "Order#"; ws.Cell(1, 2).Value = "Date"; ws.Cell(1, 3).Value = "Due Date";
        ws.Cell(1, 4).Value = "Customer"; ws.Cell(1, 5).Value = "Currency"; ws.Cell(1, 6).Value = "Total";
        ws.Cell(1, 7).Value = "Status"; ws.Cell(1, 8).Value = "Qty Ordered"; ws.Cell(1, 9).Value = "Qty Delivered";
        ws.Cell(1, 10).Value = "Qty Pending"; ws.Cell(1, 11).Value = "Fulfillment %"; ws.Cell(1, 12).Value = "Overdue";
        StyleHeader(ws, 12);

        int row = 2;
        foreach (var o in report.Orders)
        {
            ws.Cell(row, 1).Value = o.DocNum;
            ws.Cell(row, 2).Value = o.OrderDate.ToString("dd MMM yyyy");
            ws.Cell(row, 3).Value = o.DueDate.ToString("dd MMM yyyy");
            ws.Cell(row, 4).Value = $"{o.CardName} ({o.CardCode})";
            ws.Cell(row, 5).Value = o.DocCurrency;
            ws.Cell(row, 6).Value = o.OrderTotal; ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 7).Value = o.Status;
            ws.Cell(row, 8).Value = o.TotalQuantityOrdered; ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 9).Value = o.TotalQuantityDelivered; ws.Cell(row, 9).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 10).Value = o.TotalQuantityPending; ws.Cell(row, 10).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 11).Value = o.FulfillmentPercent; ws.Cell(row, 11).Style.NumberFormat.Format = "0.0\"%\"";
            ws.Cell(row, 12).Value = o.IsOverdue ? $"Yes ({o.DaysOverdue}d)" : "No";
            if (o.IsOverdue) ws.Cell(row, 12).Style.Font.FontColor = XLColor.Red;
            row++;
        }
        StyleDataRows(ws, row - 1, 12);
        FinalizeSheet(ws, 12);

        // Customer sheet
        var cws = workbook.Worksheets.Add("By Customer");
        cws.Cell(1, 1).Value = "Customer"; cws.Cell(1, 2).Value = "Code"; cws.Cell(1, 3).Value = "Total Orders";
        cws.Cell(1, 4).Value = "Open"; cws.Cell(1, 5).Value = "Closed"; cws.Cell(1, 6).Value = "Order Value";
        cws.Cell(1, 7).Value = "Fulfillment %"; cws.Cell(1, 8).Value = "Pending Value";
        StyleHeader(cws, 8);

        row = 2;
        foreach (var c in report.FulfillmentByCustomer)
        {
            cws.Cell(row, 1).Value = c.CardName;
            cws.Cell(row, 2).Value = c.CardCode;
            cws.Cell(row, 3).Value = c.TotalOrders;
            cws.Cell(row, 4).Value = c.OpenOrders;
            cws.Cell(row, 5).Value = c.ClosedOrders;
            cws.Cell(row, 6).Value = c.TotalOrderValue; cws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            cws.Cell(row, 7).Value = c.FulfillmentRatePercent; cws.Cell(row, 7).Style.NumberFormat.Format = "0.0\"%\"";
            cws.Cell(row, 8).Value = c.TotalPendingValue; cws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }
        StyleDataRows(cws, row - 1, 8);
        FinalizeSheet(cws, 8);

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportCreditNoteSummaryToExcel(CreditNoteSummaryReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Credit Notes Summary");

        ws.Cell(1, 1).Value = "Total Credit Notes"; ws.Cell(1, 2).Value = report.TotalCreditNotes;
        ws.Cell(2, 1).Value = "Total Amount (USD)"; ws.Cell(2, 2).Value = report.TotalCreditAmountUSD; ws.Cell(2, 2).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(3, 1).Value = "Total Amount (ZIG)"; ws.Cell(3, 2).Value = report.TotalCreditAmountZIG; ws.Cell(3, 2).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(4, 1).Value = "Credit-to-Sales Ratio"; ws.Cell(4, 2).Value = report.CreditToSalesRatioPercent / 100; ws.Cell(4, 2).Style.NumberFormat.Format = "0.0%";

        if (report.ByCustomer.Any())
        {
            var cws = workbook.Worksheets.Add("By Customer");
            cws.Cell(1, 1).Value = "Customer Code"; cws.Cell(1, 2).Value = "Customer Name"; cws.Cell(1, 3).Value = "Count";
            cws.Cell(1, 4).Value = "Amount (USD)"; cws.Cell(1, 5).Value = "Amount (ZIG)";
            StyleHeader(cws, 5);
            int row = 2;
            foreach (var c in report.ByCustomer.OrderByDescending(x => x.TotalAmountUSD))
            {
                cws.Cell(row, 1).Value = c.CardCode; cws.Cell(row, 2).Value = c.CardName;
                cws.Cell(row, 3).Value = c.CreditNoteCount;
                cws.Cell(row, 4).Value = c.TotalAmountUSD; cws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
                cws.Cell(row, 5).Value = c.TotalAmountZIG; cws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }
            StyleDataRows(cws, row - 1, 5);
            FinalizeSheet(cws, 5);
        }

        if (report.TopProductsReturned.Any())
        {
            var pws = workbook.Worksheets.Add("Products Returned");
            pws.Cell(1, 1).Value = "Item Code"; pws.Cell(1, 2).Value = "Item Name";
            pws.Cell(1, 3).Value = "Qty Returned"; pws.Cell(1, 4).Value = "Value (USD)";
            StyleHeader(pws, 4);
            int row = 2;
            foreach (var p in report.TopProductsReturned)
            {
                pws.Cell(row, 1).Value = p.ItemCode; pws.Cell(row, 2).Value = p.ItemName;
                pws.Cell(row, 3).Value = p.TotalQuantityReturned; pws.Cell(row, 3).Style.NumberFormat.Format = "#,##0";
                pws.Cell(row, 4).Value = p.TotalCreditAmountUSD; pws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }
            StyleDataRows(pws, row - 1, 4);
            FinalizeSheet(pws, 4);
        }

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportPurchaseOrderSummaryToExcel(PurchaseOrderSummaryReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Purchase Orders Summary");

        ws.Cell(1, 1).Value = "Total POs"; ws.Cell(1, 2).Value = report.TotalPurchaseOrders;
        ws.Cell(2, 1).Value = "Total (USD)"; ws.Cell(2, 2).Value = report.TotalOrderValueUSD; ws.Cell(2, 2).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(3, 1).Value = "Total (ZIG)"; ws.Cell(3, 2).Value = report.TotalOrderValueZIG; ws.Cell(3, 2).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(4, 1).Value = "Open POs"; ws.Cell(4, 2).Value = report.OpenOrders;
        ws.Cell(5, 1).Value = "Open Amount (USD)"; ws.Cell(5, 2).Value = report.TotalPendingValueUSD; ws.Cell(5, 2).Style.NumberFormat.Format = "#,##0.00";

        if (report.BySupplier.Any())
        {
            var sws = workbook.Worksheets.Add("By Supplier");
            sws.Cell(1, 1).Value = "Supplier Code"; sws.Cell(1, 2).Value = "Supplier Name"; sws.Cell(1, 3).Value = "POs";
            sws.Cell(1, 4).Value = "Amount (USD)"; sws.Cell(1, 5).Value = "Amount (ZIG)"; sws.Cell(1, 6).Value = "Open POs";
            StyleHeader(sws, 6);
            int row = 2;
            foreach (var s in report.BySupplier.OrderByDescending(x => x.TotalValueUSD))
            {
                sws.Cell(row, 1).Value = s.CardCode; sws.Cell(row, 2).Value = s.CardName;
                sws.Cell(row, 3).Value = s.OrderCount;
                sws.Cell(row, 4).Value = s.TotalValueUSD; sws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
                sws.Cell(row, 5).Value = s.TotalValueZIG; sws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
                sws.Cell(row, 6).Value = s.OpenOrders;
                row++;
            }
            StyleDataRows(sws, row - 1, 6);
            FinalizeSheet(sws, 6);
        }

        if (report.TopProducts.Any())
        {
            var pws = workbook.Worksheets.Add("Top Products");
            pws.Cell(1, 1).Value = "Item Code"; pws.Cell(1, 2).Value = "Item Name";
            pws.Cell(1, 3).Value = "Qty Ordered"; pws.Cell(1, 4).Value = "Value (USD)";
            StyleHeader(pws, 4);
            int row = 2;
            foreach (var p in report.TopProducts)
            {
                pws.Cell(row, 1).Value = p.ItemCode; pws.Cell(row, 2).Value = p.ItemName;
                pws.Cell(row, 3).Value = p.TotalQuantityOrdered; pws.Cell(row, 3).Style.NumberFormat.Format = "#,##0";
                pws.Cell(row, 4).Value = p.TotalCostUSD; pws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }
            StyleDataRows(pws, row - 1, 4);
            FinalizeSheet(pws, 4);
        }

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportReceivablesAgingToExcel(ReceivablesAgingReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Aging Summary");

        ws.Cell(1, 1).Value = "Bucket"; ws.Cell(1, 2).Value = "Invoices"; ws.Cell(1, 3).Value = "Amount (USD)"; ws.Cell(1, 4).Value = "% of Total";
        StyleHeader(ws, 4);
        ws.Cell(2, 1).Value = "Current (0-30 days)"; ws.Cell(2, 2).Value = report.Current.InvoiceCount; ws.Cell(2, 3).Value = report.Current.AmountUSD; ws.Cell(2, 3).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(2, 4).Value = report.Current.PercentOfTotal / 100; ws.Cell(2, 4).Style.NumberFormat.Format = "0.0%";
        ws.Cell(3, 1).Value = "31-60 days"; ws.Cell(3, 2).Value = report.Days31To60.InvoiceCount; ws.Cell(3, 3).Value = report.Days31To60.AmountUSD; ws.Cell(3, 3).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(3, 4).Value = report.Days31To60.PercentOfTotal / 100; ws.Cell(3, 4).Style.NumberFormat.Format = "0.0%";
        ws.Cell(4, 1).Value = "61-90 days"; ws.Cell(4, 2).Value = report.Days61To90.InvoiceCount; ws.Cell(4, 3).Value = report.Days61To90.AmountUSD; ws.Cell(4, 3).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(4, 4).Value = report.Days61To90.PercentOfTotal / 100; ws.Cell(4, 4).Style.NumberFormat.Format = "0.0%";
        ws.Cell(5, 1).Value = "Over 90 days"; ws.Cell(5, 2).Value = report.Over90Days.InvoiceCount; ws.Cell(5, 3).Value = report.Over90Days.AmountUSD; ws.Cell(5, 3).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(5, 4).Value = report.Over90Days.PercentOfTotal / 100; ws.Cell(5, 4).Style.NumberFormat.Format = "0.0%";
        ws.Cell(6, 1).Value = "TOTAL"; ws.Cell(6, 1).Style.Font.Bold = true; ws.Cell(6, 3).Value = report.TotalOutstandingUSD; ws.Cell(6, 3).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(6, 3).Style.Font.Bold = true;
        FinalizeSheet(ws, 4);

        if (report.CustomerAging.Any())
        {
            var cws = workbook.Worksheets.Add("Customer Aging");
            cws.Cell(1, 1).Value = "Customer Code"; cws.Cell(1, 2).Value = "Customer Name"; cws.Cell(1, 3).Value = "Total Owed";
            cws.Cell(1, 4).Value = "Current"; cws.Cell(1, 5).Value = "31-60 days"; cws.Cell(1, 6).Value = "61-90 days";
            cws.Cell(1, 7).Value = "Over 90 days"; cws.Cell(1, 8).Value = "Invoices";
            StyleHeader(cws, 8);
            int row = 2;
            foreach (var c in report.CustomerAging.OrderByDescending(x => x.TotalOutstandingUSD))
            {
                cws.Cell(row, 1).Value = c.CardCode; cws.Cell(row, 2).Value = c.CardName;
                cws.Cell(row, 3).Value = c.TotalOutstandingUSD; cws.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
                cws.Cell(row, 4).Value = c.CurrentUSD; cws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
                cws.Cell(row, 5).Value = c.Days31To60USD; cws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
                cws.Cell(row, 6).Value = c.Days61To90USD; cws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
                cws.Cell(row, 7).Value = c.Over90DaysUSD; cws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
                cws.Cell(row, 8).Value = c.TotalInvoices;
                row++;
            }
            StyleDataRows(cws, row - 1, 8);
            FinalizeSheet(cws, 8);
        }

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportProfitOverviewToExcel(ProfitOverviewReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Profit Overview");

        ws.Cell(1, 1).Value = "Metric"; ws.Cell(1, 2).Value = "USD"; ws.Cell(1, 3).Value = "ZIG";
        StyleHeader(ws, 3);
        ws.Cell(2, 1).Value = "Gross Sales"; ws.Cell(2, 2).Value = report.TotalRevenueUSD; ws.Cell(2, 3).Value = report.TotalRevenueZIG;
        ws.Cell(3, 1).Value = "Credit Notes"; ws.Cell(3, 2).Value = report.TotalCreditNotesUSD; ws.Cell(3, 3).Value = report.TotalCreditNotesZIG;
        ws.Cell(4, 1).Value = "Net Revenue"; ws.Cell(4, 2).Value = report.NetRevenueUSD; ws.Cell(4, 3).Value = report.NetRevenueZIG;
        ws.Cell(4, 1).Style.Font.Bold = true; ws.Cell(4, 2).Style.Font.Bold = true;
        ws.Cell(5, 1).Value = "Purchases (COGS)"; ws.Cell(5, 2).Value = report.TotalPurchaseCostUSD; ws.Cell(5, 3).Value = report.TotalPurchaseCostZIG;
        ws.Cell(6, 1).Value = "Gross Profit"; ws.Cell(6, 2).Value = report.GrossProfitUSD; ws.Cell(6, 3).Value = report.GrossProfitZIG;
        ws.Cell(6, 1).Style.Font.Bold = true; ws.Cell(6, 2).Style.Font.Bold = true;
        ws.Cell(7, 1).Value = "Gross Margin %"; ws.Cell(7, 2).Value = report.GrossMarginPercent / 100; ws.Cell(7, 2).Style.NumberFormat.Format = "0.0%";
        ws.Cell(8, 1).Value = "Payments Received"; ws.Cell(8, 2).Value = report.TotalCollectedUSD; ws.Cell(8, 3).Value = report.TotalCollectedZIG;
        ws.Cell(9, 1).Value = "Outstanding AR"; ws.Cell(9, 2).Value = report.OutstandingReceivablesUSD; ws.Cell(9, 3).Value = report.OutstandingReceivablesZIG;
        ws.Cell(10, 1).Value = "Collection Rate %"; ws.Cell(10, 2).Value = report.CollectionRatePercent / 100; ws.Cell(10, 2).Style.NumberFormat.Format = "0.0%";
        for (int r = 2; r <= 10; r++) { ws.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00"; if (r != 7 && r != 10) ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00"; }
        FinalizeSheet(ws, 3);

        if (report.MonthlyBreakdown.Any())
        {
            var mws = workbook.Worksheets.Add("Monthly Breakdown");
            mws.Cell(1, 1).Value = "Month"; mws.Cell(1, 2).Value = "Sales (USD)"; mws.Cell(1, 3).Value = "Credit Notes";
            mws.Cell(1, 4).Value = "Net Revenue"; mws.Cell(1, 5).Value = "Purchases"; mws.Cell(1, 6).Value = "Gross Profit"; mws.Cell(1, 7).Value = "Margin %";
            StyleHeader(mws, 7);
            int row = 2;
            foreach (var m in report.MonthlyBreakdown.OrderByDescending(x => x.Month))
            {
                var net = m.RevenueUSD - m.CreditNotesUSD;
                var gp = net - m.PurchaseCostUSD;
                mws.Cell(row, 1).Value = m.Month;
                mws.Cell(row, 2).Value = m.RevenueUSD; mws.Cell(row, 2).Style.NumberFormat.Format = "#,##0.00";
                mws.Cell(row, 3).Value = m.CreditNotesUSD; mws.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
                mws.Cell(row, 4).Value = net; mws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
                mws.Cell(row, 5).Value = m.PurchaseCostUSD; mws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
                mws.Cell(row, 6).Value = gp; mws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
                mws.Cell(row, 7).Value = net > 0 ? gp / net : 0; mws.Cell(row, 7).Style.NumberFormat.Format = "0.0%";
                row++;
            }
            StyleDataRows(mws, row - 1, 7);
            FinalizeSheet(mws, 7);
        }

        return WorkbookToBytes(workbook);
    }

    public byte[] ExportSlowMovingProductsToExcel(SlowMovingProductsReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Slow Moving Products");

        ws.Cell(1, 1).Value = "Item Code"; ws.Cell(1, 2).Value = "Item Name"; ws.Cell(1, 3).Value = "Current Stock";
        ws.Cell(1, 4).Value = "Last Sale Date"; ws.Cell(1, 5).Value = "Days Since Sale"; ws.Cell(1, 6).Value = "Stock Value";
        StyleHeader(ws, 6);
        int row = 2;
        foreach (var p in report.Products.OrderByDescending(x => x.DaysSinceLastSale))
        {
            ws.Cell(row, 1).Value = p.ItemCode; ws.Cell(row, 2).Value = p.ItemName;
            ws.Cell(row, 3).Value = p.CurrentStock; ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0";
            ws.Cell(row, 4).Value = p.LastSoldDate?.ToString("dd MMM yyyy") ?? "Never";
            ws.Cell(row, 5).Value = p.DaysSinceLastSale;
            ws.Cell(row, 6).Value = p.StockValue; ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }
        StyleDataRows(ws, row - 1, 6);
        FinalizeSheet(ws, 6);

        return WorkbookToBytes(workbook);
    }

    public string GeneratePrintableHtml(string title, string content, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var period = fromDate.HasValue && toDate.HasValue
            ? $"<p style='color:#666;margin:0 0 20px 0;'>Period: {fromDate:dd MMM yyyy} - {toDate:dd MMM yyyy}</p>"
            : $"<p style='color:#666;margin:0 0 20px 0;'>Generated: {DateTime.Now:dd MMM yyyy HH:mm}</p>";

        return $@"<!DOCTYPE html>
<html><head><meta charset='utf-8'/>
<title>{title}</title>
<style>
  @page {{ margin: 15mm; }}
  body {{ font-family: 'Segoe UI', Arial, sans-serif; color: #333; }}
  h1 {{ color: #1a237e; font-size: 22px; border-bottom: 3px solid #1a237e; padding-bottom: 8px; }}
  table {{ width: 100%; border-collapse: collapse; margin: 15px 0; font-size: 12px; }}
  th {{ background: #1a237e; color: white; padding: 10px 8px; text-align: left; }}
  td {{ padding: 8px; border-bottom: 1px solid #e0e0e0; }}
  tr:nth-child(even) {{ background: #f5f5f5; }}
  .text-end {{ text-align: right; }}
  .text-success {{ color: #2e7d32; }}
  .text-danger {{ color: #c62828; }}
  .text-info {{ color: #0277bd; }}
  .badge {{ display: inline-block; padding: 3px 8px; border-radius: 4px; font-size: 11px; font-weight: 600; }}
  .badge-danger {{ background: #c62828; color: white; }}
  .badge-warning {{ background: #f57f17; color: white; }}
  .kpi-row {{ display: flex; gap: 15px; margin: 15px 0; }}
  .kpi {{ flex: 1; background: #f8f9fa; border-radius: 8px; padding: 15px; text-align: center; border-left: 4px solid #1a237e; }}
  .kpi h3 {{ margin: 0; font-size: 24px; color: #1a237e; }}
  .kpi p {{ margin: 4px 0 0; font-size: 12px; color: #666; }}
  .footer {{ margin-top: 30px; padding-top: 10px; border-top: 1px solid #ccc; font-size: 10px; color: #999; text-align: center; }}
</style></head><body>
<h1>{title}</h1>
{period}
{content}
<div class='footer'>Shop Inventory Management System &bull; Generated {DateTime.Now:dd MMM yyyy HH:mm}</div>
</body></html>";
    }

    private static byte[] WorkbookToBytes(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
