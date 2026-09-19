using ClosedXML.Excel;
using ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport;

namespace ShopInventory.Web.Services;

/// <summary>The management sales report's workbook.</summary>
/// <remarks>
/// One sheet per section of the page. Every table sheet is written by <see cref="WriteManagementTable{T}"/>
/// from a list of columns, so a column added to one breakdown is one line, and the breakdowns cannot
/// drift apart in how they state a change or a margin. Each row carries its currency; no total is ever
/// taken across that column.
/// </remarks>
public partial class ReportExportService
{
    private sealed record ManagementColumn<T>(string Header, Func<T, object?> Value, string? Format = null);

    public byte[] ExportManagementSalesReportToExcel(
        ManagementSalesReportResult report,
        IReadOnlyDictionary<int, string>? itemGroupNames = null)
    {
        using var workbook = NewWorkbook("Management Sales Report");
        var scope = ManagementScope(report);

        WriteManagementSummary(workbook, report, scope);
        WriteManagementHealth(workbook, report, scope);

        WriteManagementTable(workbook, "By Day", "Sales by Day, against the Previous Period", scope, report,
            section => section.ByDay,
            [
                new("Day", day => day.Date, FormatDayDate),
                new("Sales", day => day.SalesCount, FormatCount),
                new("Takings", day => day.TotalAmount, FormatMoney),
                new("Compared Day", day => day.ComparedDate, FormatDayDate),
                new("Compared Sales", day => day.ComparedSalesCount, FormatCount),
                new("Compared Takings", day => day.ComparedTotalAmount, FormatMoney),
            ]);

        foreach (var (sheet, title, heading, rows) in new (string, string, string, Func<ManagementCurrencySection, List<ManagementBreakdownRow>>)[]
                 {
                     ("By Partner", "Sales by Business Partner", "Business Partner", s => s.ByPartner),
                     ("By Channel", "Sales by Channel", "Channel", s => s.ByChannel),
                     ("By Warehouse", "Sales by Warehouse", "Warehouse", s => s.ByDepot),
                     ("By Vendor", "Sales by Vendor", "Vendor", s => s.ByVendor),
                     ("By Cost Centre", "Sales by Cost Centre", "Cost Centre", s => s.ByCostCentre),
                     ("By Operator", "Sales by Operator", "Operator", s => s.ByOperator),
                     ("By Payment", "Sales by Payment Method", "Payment Method", s => s.ByPaymentMethod),
                 })
        {
            WriteManagementTable(workbook, sheet, title, scope, report, rows, BreakdownColumns(heading));
        }

        WriteManagementTable(workbook, "Lapsed Vendors", "Vendors Who Bought Last Period and Not This One", scope, report,
            section => section.LapsedVendors,
            [
                new("Vendor", row => row.Label),
                new("Code", row => row.Hint),
                new("Previous Sales", row => row.PreviousSalesCount, FormatCount),
                new("Previous Takings", row => row.PreviousTotalAmount, FormatMoney),
                new("Last Sale", row => row.LastSaleDate, FormatDate),
            ]);

        WriteManagementTable(workbook, "Products", "Sales by Product", scope, report,
            section => section.ByItem,
            [
                new("Item Code", row => row.ItemCode),
                new("Description", row => row.ItemDescription),
                new("Item Group", row => ItemGroupName(row.ItemsGroupCode, itemGroupNames)),
                new("Quantity", row => row.Quantity, FormatQuantity),
                new("Quantity Change", row => row.QuantityChangePercent / 100m, FormatPercent),
                new("Sales", row => row.SalesCount, FormatCount),
                new("Units per Sale", row => row.UnitsPerSale, FormatQuantity),
                new("Avg Price", row => row.AverageUnitPrice, FormatUnitPrice),
                new("Previous Avg Price", row => row.PreviousAverageUnitPrice, FormatUnitPrice),
                new("Price Change", row => row.PriceChangePercent / 100m, FormatPercent),
                new("Discount", row => row.DiscountAmount, FormatMoney),
                new("Value before VAT", row => row.NetAmount, FormatMoney),
                new("Share", row => row.ShareOfNetPercent / 100m, FormatPercent),
                new("Previous Quantity", row => row.PreviousQuantity, FormatQuantity),
                new("Previous Value", row => row.PreviousNetAmount, FormatMoney),
                new("Change", row => row.ChangePercent / 100m, FormatPercent),
                new("Gross Profit", row => row.GrossProfit, FormatMoney),
                new("Margin", row => row.MarginPercent / 100m, FormatPercent),
            ]);

        WriteManagementTable(workbook, "Item Groups", "Sales by Item Group", scope, report,
            section => section.ByItemGroup,
            [
                new("Item Group", row => ItemGroupName(row.ItemsGroupCode, itemGroupNames)),
                new("Group Code", row => row.ItemsGroupCode),
                new("Items", row => row.ItemCount, FormatCount),
                new("Quantity", row => row.Quantity, FormatQuantity),
                new("Value before VAT", row => row.NetAmount, FormatMoney),
                new("Share", row => row.ShareOfNetPercent / 100m, FormatPercent),
                new("Previous Quantity", row => row.PreviousQuantity, FormatQuantity),
                new("Previous Value", row => row.PreviousNetAmount, FormatMoney),
                new("Change", row => row.ChangePercent / 100m, FormatPercent),
                new("Gross Profit", row => row.GrossProfit, FormatMoney),
                new("Margin", row => row.MarginPercent / 100m, FormatPercent),
            ]);

        // Long form rather than a pivot: one row per item and partner (or warehouse), so it filters and
        // pivots in Excel without a column per partner that changes with every period's trading.
        var partnerNames = report.Partners.ToDictionary(p => p.CardCode, p => p.CardName, StringComparer.OrdinalIgnoreCase);
        WriteManagementTable(workbook, "Item x Partner", "Item Sales by Business Partner", scope, report,
            section => section.ItemPartnerMatrix,
            [
                new("Item Code", cell => cell.ItemCode),
                new("Partner Code", cell => cell.CardCode),
                new("Business Partner", cell => partnerNames.GetValueOrDefault(cell.CardCode)),
                new("Quantity", cell => cell.Quantity, FormatQuantity),
                new("Value before VAT", cell => cell.NetAmount, FormatMoney),
            ]);

        WriteManagementTable(workbook, "Item x Warehouse", "Item Sales by Warehouse", scope, report,
            section => section.ItemDepotMatrix,
            [
                new("Item Code", cell => cell.ItemCode),
                new("Warehouse", cell => cell.WarehouseCode),
                new("Quantity", cell => cell.Quantity, FormatQuantity),
                new("Value before VAT", cell => cell.NetAmount, FormatMoney),
            ]);

        return WorkbookToBytes(workbook);
    }

    private const string FormatUnitPrice = "#,##0.00##";

    private static string ItemGroupName(int? code, IReadOnlyDictionary<int, string>? names) =>
        code is null ? "Not in the product master"
        : names is not null && names.TryGetValue(code.Value, out var name) ? name
        : $"Group {code}";

    private static List<ManagementColumn<ManagementBreakdownRow>> BreakdownColumns(string heading) =>
    [
        new(heading, row => row.Label),
        new("Detail", row => row.Hint),
        new("Sales", row => row.SalesCount, FormatCount),
        new("Takings", row => row.TotalAmount, FormatMoney),
        new("Before VAT", row => row.NetAmount, FormatMoney),
        new("Share", row => row.ShareOfValuePercent / 100m, FormatPercent),
        new("Previous Sales", row => row.PreviousSalesCount, FormatCount),
        new("Previous Takings", row => row.PreviousTotalAmount, FormatMoney),
        new("Change", row => row.ChangePercent / 100m, FormatPercent),
        new("Gross Profit", row => row.GrossProfit, FormatMoney),
        new("Margin", row => row.MarginPercent / 100m, FormatPercent),
    ];

    private static string ManagementScope(ManagementSalesReportResult report)
    {
        var partner = report.CardCode is { Length: > 0 } code
            ? $"Business partner: {report.Partners.FirstOrDefault(p => string.Equals(p.CardCode, code, StringComparison.OrdinalIgnoreCase))?.CardName ?? code} ({code})"
            : "All business partners";
        var where = string.IsNullOrWhiteSpace(report.WarehouseCode) ? partner : $"{partner} · warehouse {report.WarehouseCode}";
        var channel = string.IsNullOrWhiteSpace(report.SourceSystem) ? "all channels" : report.SourceSystem;
        return $"{where} · {channel} · compared with {report.PreviousFromDate:dd MMM} – {report.PreviousToDate:dd MMM yyyy}";
    }

    private static void WriteManagementSummary(XLWorkbook workbook, ManagementSalesReportResult report, string scope)
    {
        var rows = report.Currencies.SelectMany(section => new (string Currency, string Measure, object? Now, object? Before, decimal? Change, string Format)[]
        {
            (section.Currency, "Takings", section.Summary.TotalAmount, section.Summary.PreviousTotalAmount, section.Summary.TotalChangePercent, FormatMoney),
            (section.Currency, "Takings before VAT", section.Summary.NetAmount, section.Summary.PreviousNetAmount, null, FormatMoney),
            (section.Currency, "Sales", section.Summary.SalesCount, section.Summary.PreviousSalesCount, section.Summary.SalesCountChangePercent, FormatCount),
            (section.Currency, "Average sale", section.Summary.AverageSale, section.Summary.PreviousAverageSale, section.Summary.AverageSaleChangePercent, FormatMoney),
            (section.Currency, "Vendors served", section.Summary.VendorsServed, section.Summary.PreviousVendorsServed, null, FormatCount),
            (section.Currency, "Days traded", section.Summary.DaysTraded, null, null, FormatCount),
            (section.Currency, "Gross profit (SAP)", section.Summary.GrossProfit, null, null, FormatMoney),
            (section.Currency, "Margin on costed sales", section.Summary.MarginPercent / 100m, null, null, FormatPercent),
            (section.Currency, "Revenue SAP costed", section.Summary.CostedNetAmount, null, null, FormatMoney),
            (section.Currency, "Sales SAP costed", section.Summary.CostedSalesCount, null, null, FormatCount),
        }).ToList();

        const int cols = 5;
        var ws = AddSheet(workbook, "Summary");
        var row = WriteReportHeader(ws, "Management Sales Report", cols, report.FromDate, report.ToDate, scope);

        string[] headers = ["Currency", "Measure", "This Period", "Previous Period", "Change"];
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row++;
        var dataStart = row;

        foreach (var line in rows)
        {
            ws.Cell(row, 1).Value = line.Currency;
            ws.Cell(row, 2).Value = line.Measure;
            SetManagementCell(ws.Cell(row, 3), line.Now, line.Format);
            SetManagementCell(ws.Cell(row, 4), line.Before, line.Format);
            SetManagementCell(ws.Cell(row, 5), line.Change / 100m, FormatPercent);
            row++;
        }

        row = FinishTable(ws, headerRow, dataStart, row, cols, "No sales fell in this period.", filter: false);

        ws.Range(row, 1, row, cols).Merge();
        ws.Cell(row, 1).Value = report.Margin.Detail;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = report.Margin.Available ? MutedText : WarningOrange;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 45;

        WriteFooter(ws, row + 1, cols);
        FinalizeSheet(ws, cols, headerRow);
    }

    private static void WriteManagementHealth(XLWorkbook workbook, ManagementSalesReportResult report, string scope)
    {
        var health = report.Health;
        var buckets = new (string Stage, string State, ManagementHealthBucket Bucket)[]
        {
            ("Fiscalisation", "Fiscalised", health.FiscalSucceeded),
            ("Fiscalisation", "Waiting", health.FiscalPending),
            ("Fiscalisation", "Failed", health.FiscalFailed),
            ("Fiscalisation", "Skipped", health.FiscalSkipped),
            ("Fiscalisation", "Needs reconciliation", health.NeedsReconciliation),
            ("SAP posting", "Posted", health.Posted),
            ("SAP posting", "Waiting", health.PostingWaiting),
            ("SAP posting", "Failing", health.PostingFailing),
            ("SAP payment", "Failed", health.PaymentFailed),
        };

        const int cols = 5;
        var ws = AddSheet(workbook, "Posting & Fiscal");
        var row = WriteReportHeader(ws, "Posting and Fiscalisation", cols, report.FromDate, report.ToDate, scope);

        string[] headers = ["Stage", "State", "Sales", "Currency", "Value"];
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row++;
        var dataStart = row;

        foreach (var (stage, state, bucket) in buckets)
        {
            var values = bucket.Value.Count == 0 ? [new ManagementCurrencyAmount()] : bucket.Value;
            foreach (var value in values)
            {
                ws.Cell(row, 1).Value = stage;
                ws.Cell(row, 2).Value = state;
                ws.Cell(row, 3).Value = bucket.SalesCount;
                ws.Cell(row, 3).Style.NumberFormat.Format = FormatCount;
                ws.Cell(row, 4).Value = value.Currency;
                ws.Cell(row, 5).Value = value.Amount;
                ws.Cell(row, 5).Style.NumberFormat.Format = FormatMoney;
                if (bucket.SalesCount > 0 && state is "Failed" or "Failing" or "Needs reconciliation")
                {
                    ws.Range(row, 1, row, cols).Style.Font.FontColor = DangerRed;
                }
                row++;
            }
        }

        row = FinishTable(ws, headerRow, dataStart, row, cols, filter: false);

        var notes = new List<string>
        {
            $"{health.PostingSalesCount:N0} of {health.SalesCount:N0} sale(s) are posted by the desktop posting job (till and vending); van sales reach SAP through the van workflow.",
        };
        if (health.OldestUnpostedDate is { } oldest)
        {
            notes.Add($"Oldest sale SAP does not have yet: {oldest:dd MMM yyyy}.");
        }
        if (!string.IsNullOrWhiteSpace(health.LatestPostingError))
        {
            notes.Add($"Latest SAP refusal: {health.LatestPostingError}");
        }

        foreach (var note in notes)
        {
            ws.Range(row, 1, row, cols).Merge();
            ws.Cell(row, 1).Value = note;
            ws.Cell(row, 1).Style.Font.FontColor = MutedText;
            ws.Cell(row, 1).Style.Alignment.WrapText = true;
            ws.Row(row).Height = 30;
            row++;
        }

        WriteFooter(ws, row, cols);
        FinalizeSheet(ws, cols, headerRow);
    }

    private static void WriteManagementTable<T>(
        XLWorkbook workbook,
        string sheetName,
        string title,
        string scope,
        ManagementSalesReportResult report,
        Func<ManagementCurrencySection, IEnumerable<T>> rows,
        IReadOnlyList<ManagementColumn<T>> columns)
    {
        var cols = columns.Count + 1;
        var ws = AddSheet(workbook, sheetName);
        var row = WriteReportHeader(ws, title, cols, report.FromDate, report.ToDate, scope);

        ws.Cell(row, 1).Value = "Currency";
        for (var i = 0; i < columns.Count; i++)
        {
            ws.Cell(row, i + 2).Value = columns[i].Header;
        }
        StyleTableHeader(ws, row, cols);
        var headerRow = row++;
        var dataStart = row;

        foreach (var section in report.Currencies)
        {
            foreach (var line in rows(section))
            {
                ws.Cell(row, 1).Value = section.Currency;
                ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                for (var i = 0; i < columns.Count; i++)
                {
                    SetManagementCell(ws.Cell(row, i + 2), columns[i].Value(line), columns[i].Format);
                }
                row++;
            }
        }

        row = FinishTable(ws, headerRow, dataStart, row, cols, "No sales fell in this period.");
        WriteFooter(ws, row - 1, cols);
        FinalizeSheet(ws, cols, headerRow, landscape: cols > 6);
    }

    /// <summary>Writes a value of any of the report's types; a null leaves the cell blank rather than zero.</summary>
    private static void SetManagementCell(IXLCell cell, object? value, string? format)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                cell.Value = text;
                return;
            case int count:
                cell.Value = count;
                break;
            case decimal amount:
                cell.Value = amount;
                break;
            case DateTime date:
                cell.Value = date;
                break;
            default:
                cell.Value = value.ToString();
                return;
        }

        if (format is not null)
        {
            cell.Style.NumberFormat.Format = format;
        }
    }
}
