using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ShopInventory.Web.Features.Reports.Queries.GetAccountSalesPaymentReport;
using ShopInventory.Web.Services;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AccountSalesPaymentWorkbookRestyler <input-workbook-path> [output-workbook-path]");
    return 1;
}

var inputPath = Path.GetFullPath(args[0]);
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Workbook not found: {inputPath}");
    return 1;
}

var outputPath = args.Length > 1
    ? Path.GetFullPath(args[1])
    : GetDefaultOutputPath(inputPath);

var report = LoadReportFromWorkbook(inputPath);
var exporter = new ReportExportService();
var bytes = exporter.ExportAccountSalesPaymentReportToExcel(report);

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllBytes(outputPath, bytes);

Console.WriteLine($"Created restyled workbook: {outputPath}");
Console.WriteLine($"Sheets: Dashboard, Visuals, Trend Analysis, Customer Analysis, Item Summary, Invoice Register, Payment Register, Application Map");
Console.WriteLine($"Accounts={report.AccountTotals.Count} Periods={report.Periods.Count} InvoiceLines={report.InvoiceDetails.Count} Payments={report.PaymentDetails.Count} Applications={report.PaymentApplications.Count}");

return 0;

static string GetDefaultOutputPath(string inputPath)
{
    var directory = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
    var fileName = Path.GetFileNameWithoutExtension(inputPath);
    var extension = Path.GetExtension(inputPath);
    return Path.Combine(directory, $"{fileName}_RESTYLED{extension}");
}

static GetAccountSalesPaymentReportResult LoadReportFromWorkbook(string inputPath)
{
    using var workbook = new XLWorkbook(inputPath);

    var report = new GetAccountSalesPaymentReportResult();
    ParseDashboardMetadata(workbook.Worksheet("Dashboard"), report);
    report.Periods = ParsePeriods(workbook.Worksheet("Trend Analysis"));
    report.AccountTotals = ParseAccounts(workbook.Worksheet("Customer Analysis"));
    AttachItems(report.AccountTotals, ParseItems(workbook.Worksheet("Item Summary")));
    report.InvoiceDetails = ParseInvoiceDetails(workbook.Worksheet("Invoice Register"));
    report.PaymentDetails = ParsePaymentDetails(workbook.Worksheet("Payment Register"));
    report.PaymentApplications = ParsePaymentApplications(workbook.Worksheet("Application Map"));

    BackfillReport(report);
    return report;
}

static void ParseDashboardMetadata(IXLWorksheet dashboard, GetAccountSalesPaymentReportResult report)
{
    var usedRange = dashboard.RangeUsed();
    if (usedRange is null)
    {
        return;
    }

    var requestedRegex = new Regex(@"Requested accounts:\s*(?<requested>\d+)\s*\|\s*Active:\s*(?<active>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    var sourcesRegex = new Regex(@"Sources:\s*(?<sources>.+?)\s*\|\s*Grouping:\s*(?<grouping>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    var dateRangeRegex = new Regex(@"DATE RANGE\s+(?<from>\d{2} \w{3} \d{4})\s+TO\s+(?<to>\d{2} \w{3} \d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    var generatedRegex = new Regex(@"Generated:\s*(?<generated>.+?)(?:\s+CAT)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    foreach (var cell in usedRange.CellsUsed())
    {
        var value = cell.GetString().Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            continue;
        }

        var requestedMatch = requestedRegex.Match(value);
        if (requestedMatch.Success)
        {
            report.Summary.RequestedAccountCount = ParseInt(requestedMatch.Groups["requested"].Value);
            report.Summary.ActiveAccountCount = ParseInt(requestedMatch.Groups["active"].Value);
            continue;
        }

        var sourcesMatch = sourcesRegex.Match(value);
        if (sourcesMatch.Success)
        {
            report.Sources = sourcesMatch.Groups["sources"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            report.Grouping = ParseGrouping(sourcesMatch.Groups["grouping"].Value);
            continue;
        }

        var dateRangeMatch = dateRangeRegex.Match(value);
        if (dateRangeMatch.Success)
        {
            report.FromDateUtc = ParseCatDateString(dateRangeMatch.Groups["from"].Value, includeTime: false);
            report.ToDateUtc = ParseCatDateString(dateRangeMatch.Groups["to"].Value, includeTime: false);
            continue;
        }

        var generatedMatch = generatedRegex.Match(value);
        if (generatedMatch.Success)
        {
            report.GeneratedAtUtc = ParseCatDateString(generatedMatch.Groups["generated"].Value, includeTime: true);
        }
    }
}

static List<AccountSalesPaymentPeriodResult> ParsePeriods(IXLWorksheet worksheet)
{
    var table = FindTable(
        worksheet,
        "Period",
        "Start (CAT)",
        "End (CAT)",
        "Accounts",
        "Invoices",
        "Payments",
        "Sales USD",
        "Payments USD",
        "Outstanding USD",
        "Collection USD %",
        "USD Pulse",
        "Sales ZiG",
        "Payments ZiG",
        "Outstanding ZiG");

    var periods = new List<AccountSalesPaymentPeriodResult>();
    foreach (var rowNumber in EnumerateDataRows(table))
    {
        var accountCount = GetInt(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Accounts")]));
        periods.Add(new AccountSalesPaymentPeriodResult
        {
            Label = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Period")])),
            PeriodStartUtc = GetCatDate(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Start (CAT)")])),
            PeriodEndUtc = GetCatDate(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("End (CAT)")])),
            InvoiceCount = GetInt(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Invoices")])),
            PaymentCount = GetInt(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Payments")])),
            TotalSalesUsd = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Sales USD")])),
            IncomingPaymentsUsd = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Payments USD")])),
            TotalSalesZig = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Sales ZiG")])),
            IncomingPaymentsZig = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Payments ZiG")])),
            Accounts = Enumerable.Range(0, Math.Max(0, accountCount))
                .Select(_ => new AccountSalesPaymentAccountResult())
                .ToList()
        });
    }

    return periods;
}

static List<AccountSalesPaymentAccountResult> ParseAccounts(IXLWorksheet worksheet)
{
    var table = FindTable(
        worksheet,
        "Card Code",
        "Card Name",
        "Invoices",
        "Payments",
        "Sales USD",
        "Collections USD",
        "Outstanding USD",
        "Share USD %",
        "Sales ZiG",
        "Collections ZiG",
        "Outstanding ZiG",
        "Share ZiG %",
        "Pulse",
        "Status");

    var accounts = new List<AccountSalesPaymentAccountResult>();
    foreach (var rowNumber in EnumerateDataRows(table))
    {
        var salesUsd = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Sales USD")]));
        var collectionsUsd = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Collections USD")]));
        var salesZig = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Sales ZiG")]));
        var collectionsZig = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Collections ZiG")]));

        accounts.Add(new AccountSalesPaymentAccountResult
        {
            CardCode = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Card Code")])),
            CardName = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Card Name")])),
            InvoiceCount = GetInt(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Invoices")])),
            PaymentCount = GetInt(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Payments")])),
            TotalSalesUsd = salesUsd,
            IncomingPaymentsUsd = collectionsUsd,
            TotalSalesZig = salesZig,
            IncomingPaymentsZig = collectionsZig,
            CollectionRatePercentUsd = CalculatePercent(collectionsUsd, salesUsd),
            CollectionRatePercentZig = CalculatePercent(collectionsZig, salesZig)
        });
    }

    return accounts;
}

static Dictionary<string, List<AccountSalesPaymentItemResult>> ParseItems(IXLWorksheet worksheet)
{
    var table = FindTable(
        worksheet,
        "Card Code",
        "Card Name",
        "Item Code",
        "Item Name",
        "Invoices",
        "Qty Sold",
        "Sales USD",
        "Sales ZiG",
        "Value Pulse");

    var itemsByCard = new Dictionary<string, List<AccountSalesPaymentItemResult>>(StringComparer.OrdinalIgnoreCase);
    foreach (var rowNumber in EnumerateDataRows(table))
    {
        var cardCode = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Card Code")]));
        if (!itemsByCard.TryGetValue(cardCode, out var items))
        {
            items = new List<AccountSalesPaymentItemResult>();
            itemsByCard[cardCode] = items;
        }

        items.Add(new AccountSalesPaymentItemResult
        {
            ItemCode = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Item Code")])),
            ItemName = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Item Name")])),
            InvoiceCount = GetInt(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Invoices")])),
            TotalQuantitySold = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Qty Sold")])),
            TotalSalesUsd = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Sales USD")])),
            TotalSalesZig = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Sales ZiG")]))
        });
    }

    return itemsByCard;
}

static List<AccountSalesPaymentInvoiceDetailResult> ParseInvoiceDetails(IXLWorksheet worksheet)
{
    var table = FindTable(
        worksheet,
        "Period",
        "Source",
        "Doc Date (CAT)",
        "Card Code",
        "Card Name",
        "Invoice #",
        "DocEntry",
        "Status",
        "Currency",
        "Invoice Total",
        "Value Band",
        "Line #",
        "Item Code",
        "Item Name",
        "Quantity",
        "Line Amount",
        "Sales USD",
        "Sales ZiG");

    var invoices = new List<AccountSalesPaymentInvoiceDetailResult>();
    foreach (var rowNumber in EnumerateDataRows(table))
    {
        invoices.Add(new AccountSalesPaymentInvoiceDetailResult
        {
            PeriodLabel = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Period")])),
            Source = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Source")])),
            DocumentDateUtc = GetCatDate(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Doc Date (CAT)")])),
            CardCode = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Card Code")])),
            CardName = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Card Name")])),
            DocumentNumber = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Invoice #")])),
            DocumentEntry = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("DocEntry")])),
            Status = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Status")])),
            Currency = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Currency")])),
            DocumentTotal = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Invoice Total")])),
            LineNumber = GetInt(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Line #")])),
            ItemCode = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Item Code")])),
            ItemName = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Item Name")])),
            QuantitySold = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Quantity")])),
            LineAmount = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Line Amount")])),
            SalesUsd = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Sales USD")])),
            SalesZig = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Sales ZiG")]))
        });
    }

    return invoices;
}

static List<AccountSalesPaymentPaymentDetailResult> ParsePaymentDetails(IXLWorksheet worksheet)
{
    var table = FindTable(
        worksheet,
        "Period",
        "Source",
        "Payment Date (CAT)",
        "Card Code",
        "Card Name",
        "Payment #",
        "DocEntry",
        "Status",
        "Currency",
        "Total Amount",
        "Incoming USD",
        "Incoming ZiG",
        "Applied Invoices",
        "Reference",
        "Value Band");

    var payments = new List<AccountSalesPaymentPaymentDetailResult>();
    foreach (var rowNumber in EnumerateDataRows(table))
    {
        payments.Add(new AccountSalesPaymentPaymentDetailResult
        {
            PeriodLabel = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Period")])),
            Source = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Source")])),
            PaymentDateUtc = GetCatDate(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Payment Date (CAT)")])),
            CardCode = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Card Code")])),
            CardName = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Card Name")])),
            PaymentNumber = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Payment #")])),
            PaymentEntry = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("DocEntry")])),
            Status = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Status")])),
            Currency = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Currency")])),
            TotalAmount = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Total Amount")])),
            IncomingPaymentsUsd = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Incoming USD")])),
            IncomingPaymentsZig = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Incoming ZiG")])),
            AppliedInvoiceCount = GetInt(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Applied Invoices")])),
            Reference = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Reference")]))
        });
    }

    return payments;
}

static List<AccountSalesPaymentPaymentApplicationResult> ParsePaymentApplications(IXLWorksheet worksheet)
{
    var table = FindTable(
        worksheet,
        "Period",
        "Source",
        "Payment Date (CAT)",
        "Card Code",
        "Card Name",
        "Payment #",
        "DocEntry",
        "Status",
        "Applied Invoice",
        "Invoice Type",
        "Currency",
        "Applied Amount");

    var applications = new List<AccountSalesPaymentPaymentApplicationResult>();
    foreach (var rowNumber in EnumerateDataRows(table))
    {
        applications.Add(new AccountSalesPaymentPaymentApplicationResult
        {
            PeriodLabel = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Period")])),
            Source = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Source")])),
            PaymentDateUtc = GetCatDate(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Payment Date (CAT)")])),
            CardCode = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Card Code")])),
            CardName = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Card Name")])),
            PaymentNumber = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Payment #")])),
            PaymentEntry = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("DocEntry")])),
            Status = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Status")])),
            AppliedInvoiceReference = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Applied Invoice")])),
            InvoiceType = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Invoice Type")])),
            Currency = GetText(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Currency")])),
            AppliedAmount = GetDecimal(worksheet.Cell(rowNumber, table.Columns[NormalizeHeader("Applied Amount")]))
        });
    }

    return applications;
}

static void AttachItems(List<AccountSalesPaymentAccountResult> accounts, Dictionary<string, List<AccountSalesPaymentItemResult>> itemsByCard)
{
    foreach (var account in accounts)
    {
        if (itemsByCard.TryGetValue(account.CardCode, out var items))
        {
            account.Items = items;
            account.TotalQuantitySold = items.Sum(item => item.TotalQuantitySold);
        }
    }
}

static void BackfillReport(GetAccountSalesPaymentReportResult report)
{
    report.RequestedAccountCodes = report.AccountTotals
        .Select(account => account.CardCode)
        .Where(cardCode => !string.IsNullOrWhiteSpace(cardCode))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    report.Sources = report.Sources.Count > 0
        ? report.Sources
        : report.InvoiceDetails
            .Select(detail => detail.Source)
            .Concat(report.PaymentDetails.Select(detail => detail.Source))
            .Concat(report.PaymentApplications.Select(detail => detail.Source))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("SAP")
            .ToList();

    foreach (var account in report.AccountTotals)
    {
        account.CollectionRatePercentUsd = CalculatePercent(account.IncomingPaymentsUsd, account.TotalSalesUsd);
        account.CollectionRatePercentZig = CalculatePercent(account.IncomingPaymentsZig, account.TotalSalesZig);
    }

    var periodAccountMap = report.InvoiceDetails
        .Select(detail => new { detail.PeriodLabel, detail.CardCode, detail.CardName })
        .Concat(report.PaymentDetails.Select(detail => new { detail.PeriodLabel, detail.CardCode, detail.CardName }))
        .Where(row => !string.IsNullOrWhiteSpace(row.PeriodLabel) && !string.IsNullOrWhiteSpace(row.CardCode))
        .GroupBy(row => row.PeriodLabel, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group
                .GroupBy(item => item.CardCode, StringComparer.OrdinalIgnoreCase)
                .Select(itemGroup => new AccountSalesPaymentAccountResult
                {
                    CardCode = itemGroup.Key,
                    CardName = itemGroup.Select(item => item.CardName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty
                })
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

    foreach (var period in report.Periods)
    {
        if (periodAccountMap.TryGetValue(period.Label, out var accounts))
        {
            period.Accounts = accounts;
        }

        period.TotalQuantitySold = report.InvoiceDetails
            .Where(detail => string.Equals(detail.PeriodLabel, period.Label, StringComparison.OrdinalIgnoreCase))
            .Sum(detail => detail.QuantitySold);
    }

    report.Summary.ActiveAccountCount = report.AccountTotals.Count;
    if (report.Summary.RequestedAccountCount == 0)
    {
        report.Summary.RequestedAccountCount = report.Summary.ActiveAccountCount;
    }

    report.Summary.TotalPeriods = report.Periods.Count;
    report.Summary.TotalInvoices = report.InvoiceDetails
        .GroupBy(detail => $"{detail.Source}|{detail.DocumentEntry}|{detail.DocumentNumber}", StringComparer.OrdinalIgnoreCase)
        .Count();
    report.Summary.TotalPayments = report.PaymentDetails.Count;
    report.Summary.TotalQuantitySold = report.InvoiceDetails.Sum(detail => detail.QuantitySold);
    report.Summary.TotalSalesUsd = report.AccountTotals.Sum(account => account.TotalSalesUsd);
    report.Summary.TotalSalesZig = report.AccountTotals.Sum(account => account.TotalSalesZig);
    report.Summary.TotalIncomingPaymentsUsd = report.AccountTotals.Sum(account => account.IncomingPaymentsUsd);
    report.Summary.TotalIncomingPaymentsZig = report.AccountTotals.Sum(account => account.IncomingPaymentsZig);
    report.Summary.CollectionRatePercentUsd = CalculatePercent(report.Summary.TotalIncomingPaymentsUsd, report.Summary.TotalSalesUsd);
    report.Summary.CollectionRatePercentZig = CalculatePercent(report.Summary.TotalIncomingPaymentsZig, report.Summary.TotalSalesZig);

    if (report.GeneratedAtUtc == default)
    {
        report.GeneratedAtUtc = DateTime.UtcNow;
    }

    if (report.FromDateUtc == default || report.ToDateUtc == default)
    {
        var allDates = report.Periods.Select(period => period.PeriodStartUtc)
            .Concat(report.Periods.Select(period => period.PeriodEndUtc))
            .Concat(report.InvoiceDetails.Select(detail => detail.DocumentDateUtc))
            .Concat(report.PaymentDetails.Select(detail => detail.PaymentDateUtc))
            .Where(date => date != default)
            .OrderBy(date => date)
            .ToList();

        if (allDates.Count > 0)
        {
            report.FromDateUtc = report.FromDateUtc == default ? allDates.First() : report.FromDateUtc;
            report.ToDateUtc = report.ToDateUtc == default ? allDates.Last() : report.ToDateUtc;
        }
        else
        {
            report.FromDateUtc = report.FromDateUtc == default ? DateTime.UtcNow.Date : report.FromDateUtc;
            report.ToDateUtc = report.ToDateUtc == default ? DateTime.UtcNow.Date : report.ToDateUtc;
        }
    }

    if (report.Grouping == default)
    {
        report.Grouping = InferGrouping(report.Periods);
    }
}

static AccountSalesPaymentGrouping InferGrouping(List<AccountSalesPaymentPeriodResult> periods)
{
    if (periods.Count < 2)
    {
        return AccountSalesPaymentGrouping.Daily;
    }

    var ordered = periods
        .Select(period => period.PeriodStartUtc)
        .Where(date => date != default)
        .OrderBy(date => date)
        .ToList();

    if (ordered.Count < 2)
    {
        return AccountSalesPaymentGrouping.Daily;
    }

    var averageGap = ordered.Zip(ordered.Skip(1), (first, second) => (second - first).TotalDays).Average();
    if (averageGap >= 25)
    {
        return AccountSalesPaymentGrouping.Monthly;
    }

    if (averageGap >= 6)
    {
        return AccountSalesPaymentGrouping.Weekly;
    }

    return AccountSalesPaymentGrouping.Daily;
}

static SheetTable FindTable(IXLWorksheet worksheet, params string[] requiredHeaders)
{
    var usedRange = worksheet.RangeUsed() ?? throw new InvalidOperationException($"Worksheet '{worksheet.Name}' is empty.");
    var required = requiredHeaders.Select(NormalizeHeader).ToList();

    for (var rowNumber = usedRange.RangeAddress.FirstAddress.RowNumber; rowNumber <= usedRange.RangeAddress.LastAddress.RowNumber; rowNumber++)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var columnNumber = usedRange.RangeAddress.FirstAddress.ColumnNumber; columnNumber <= usedRange.RangeAddress.LastAddress.ColumnNumber; columnNumber++)
        {
            var headerText = NormalizeHeader(worksheet.Cell(rowNumber, columnNumber).GetString());
            if (!string.IsNullOrWhiteSpace(headerText) && !columns.ContainsKey(headerText))
            {
                columns[headerText] = columnNumber;
            }
        }

        if (required.All(columns.ContainsKey))
        {
            return new SheetTable(worksheet, rowNumber, usedRange.RangeAddress.LastAddress.ColumnNumber, columns);
        }
    }

    throw new InvalidOperationException($"Could not find the expected header row on worksheet '{worksheet.Name}'.");
}

static IEnumerable<int> EnumerateDataRows(SheetTable table)
{
    var lastRow = table.Worksheet.RangeUsed()?.RangeAddress.LastAddress.RowNumber ?? table.HeaderRow;
    for (var rowNumber = table.HeaderRow + 1; rowNumber <= lastRow; rowNumber++)
    {
        if (IsFooterRow(table.Worksheet, rowNumber, table.LastColumn))
        {
            yield break;
        }

        if (IsBlankRow(table.Worksheet, rowNumber, table.LastColumn))
        {
            continue;
        }

        yield return rowNumber;
    }
}

static bool IsFooterRow(IXLWorksheet worksheet, int rowNumber, int lastColumn)
{
    var firstCell = GetText(worksheet.Cell(rowNumber, 1));
    return firstCell.StartsWith("CONFIDENTIAL", StringComparison.OrdinalIgnoreCase)
           || firstCell.StartsWith("This document was auto-generated", StringComparison.OrdinalIgnoreCase)
           || (rowNumber > 1 && IsBlankRow(worksheet, rowNumber, lastColumn));
}

static bool IsBlankRow(IXLWorksheet worksheet, int rowNumber, int lastColumn)
{
    for (var columnNumber = 1; columnNumber <= lastColumn; columnNumber++)
    {
        if (!worksheet.Cell(rowNumber, columnNumber).IsEmpty())
        {
            return false;
        }
    }

    return true;
}

static string NormalizeHeader(string value)
{
    var normalized = Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();
    return normalized switch
    {
        "USD PULSE" => "PULSE",
        "VALUE PULSE" => "PULSE",
        "VALUE BAR" => "PULSE",
        "SALES BAR" => "PULSE",
        "CONTRIBUTION BAR" => "PULSE",
        _ => normalized
    };
}

static string GetText(IXLCell cell) => cell.GetFormattedString().Trim();

static int GetInt(IXLCell cell)
{
    if (cell.TryGetValue<int>(out var intValue))
    {
        return intValue;
    }

    if (cell.TryGetValue<double>(out var doubleValue))
    {
        return Convert.ToInt32(Math.Round(doubleValue, MidpointRounding.AwayFromZero));
    }

    return ParseInt(cell.GetFormattedString());
}

static int ParseInt(string value)
{
    var cleaned = value.Replace(",", string.Empty).Trim();
    return int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}

static decimal GetDecimal(IXLCell cell)
{
    if (cell.TryGetValue<decimal>(out var decimalValue))
    {
        return decimalValue;
    }

    if (cell.TryGetValue<double>(out var doubleValue))
    {
        return Convert.ToDecimal(doubleValue);
    }

    var cleaned = cell.GetFormattedString()
        .Replace("USD", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("ZiG", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("%", string.Empty)
        .Replace(",", string.Empty)
        .Trim();

    return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out decimalValue)
        ? decimalValue
        : 0m;
}

static DateTime GetCatDate(IXLCell cell)
{
    if (cell.TryGetValue<DateTime>(out var dateValue))
    {
        return IAuditService.ToUTC(dateValue);
    }

    return ParseCatDateString(cell.GetFormattedString(), includeTime: cell.GetFormattedString().Contains(':'));
}

static DateTime ParseCatDateString(string value, bool includeTime)
{
    var cleaned = value.Replace("CAT", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
    if (string.IsNullOrWhiteSpace(cleaned))
    {
        return default;
    }

    var formats = includeTime
        ? new[] { "dd MMM yyyy HH:mm", "d MMM yyyy HH:mm", "dd MMM yyyy H:mm", "d MMM yyyy H:mm" }
        : new[] { "dd MMM yyyy", "d MMM yyyy" };

    if (DateTime.TryParseExact(cleaned, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        || DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
    {
        return IAuditService.ToUTC(parsed);
    }

    return default;
}

static AccountSalesPaymentGrouping ParseGrouping(string value)
{
    return Enum.TryParse<AccountSalesPaymentGrouping>(value.Trim(), ignoreCase: true, out var grouping)
        ? grouping
        : AccountSalesPaymentGrouping.Daily;
}

static decimal CalculatePercent(decimal numerator, decimal denominator) => denominator <= 0m
    ? 0m
    : Math.Round((numerator / denominator) * 100m, 2);

file sealed record SheetTable(IXLWorksheet Worksheet, int HeaderRow, int LastColumn, Dictionary<string, int> Columns);