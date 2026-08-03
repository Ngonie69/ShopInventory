using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Services;
using Xunit.Abstractions;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Asks a real Service Layer to accept the five reports that split takings by currency, and pins the
/// validator limit that decided their shape.
/// </summary>
/// <remarks>
/// These five — sales summary, daily sales, customer invoices, receivables aging and top products —
/// were the reports this suite could not previously cover. Every one of them computed its USD/ZiG
/// columns as <c>SUM(CASE WHEN "DocCur" = 'USD' ...)</c>, and SAP's SQLQueries validator rejects a
/// CASE expression when the query object is created: 701, "Cannot support the case expression". So
/// none of the five objects had ever existed, and each report answered HTTP 400 before reading a
/// row. Nothing upstream could see it. The SQL is valid HANA and the unit tests were green — only
/// the create call ever saw the error, which is exactly the gap this project exists to close.
///
/// With the split moved into C#, there is real SQL to exercise, so the exclusion is lifted and all
/// five run here. <see cref="A_case_expression_is_still_rejected_at_create"/> keeps the reason on
/// the record: if it ever starts failing, the limit has been lifted SAP-side and the split could
/// move back into SQL.
///
/// Like the statement tests, these reuse the same fixed query codes the reports themselves use, so
/// running them repeatedly costs no SAP objects.
/// </remarks>
[Collection("SAP")]
public class SapReportQueryTests(SapClientFixture fixture, ITestOutputHelper output)
{
    private static readonly DateTime FromDate = DateTime.UtcNow.Date.AddDays(-30);
    private static readonly DateTime ToDate = DateTime.UtcNow.Date;

    /// <summary>
    /// The whole point: every one of these calls used to fail at create, and none of the failures
    /// were visible from anywhere but a live Service Layer.
    /// </summary>
    [SapFact]
    public async Task Every_currency_split_report_is_accepted()
    {
        var reports = BuildReportService();

        var salesSummary = await reports.GetSalesSummaryAsync(FromDate, ToDate);
        output.WriteLine(
            $"sales summary: {salesSummary.TotalInvoices} invoices, {salesSummary.UniqueCustomers} customers, "
            + $"{salesSummary.DailySales.Count} days, USD {salesSummary.TotalSalesUSD}, ZiG {salesSummary.TotalSalesZIG}");

        var topProducts = await reports.GetTopProductsAsync(FromDate, ToDate, topCount: 10);
        output.WriteLine($"top products: {topProducts.TopProducts.Count} items");

        var topCustomers = await reports.GetTopCustomersAsync(FromDate, ToDate, topCount: 10);
        output.WriteLine($"top customers: {topCustomers.TotalCustomers} customers");

        // The widest of the five: the aging report takes its own 365-day range off the clock.
        var aging = await reports.GetReceivablesAgingAsync();
        output.WriteLine(
            $"aging: {aging.TotalCustomers} customers, USD {aging.TotalOutstandingUSD}, ZiG {aging.TotalOutstandingZIG}, "
            + $"buckets {aging.Current.InvoiceCount}/{aging.Days31To60.InvoiceCount}/"
            + $"{aging.Days61To90.InvoiceCount}/{aging.Over90Days.InvoiceCount}");

        var warehouses = await fixture.Client.GetWarehousesAsync();
        var warehouseCode = warehouses.FirstOrDefault()?.WarehouseCode;
        Assert.False(
            string.IsNullOrWhiteSpace(warehouseCode),
            "SAP returned no warehouses, so the warehouse-filtered top-products statement cannot be exercised.");

        var byWarehouse = await reports.GetTopProductsAsync(FromDate, ToDate, topCount: 10, warehouseCode);
        output.WriteLine($"top products in {warehouseCode}: {byWarehouse.TopProducts.Count} items");
    }

    /// <summary>
    /// Every column the pivots read has to come back under the name they read it by. A renamed alias
    /// is not an error anywhere — the row simply has no such key, and the report quietly reports
    /// zero.
    /// </summary>
    [SapFact]
    public async Task The_statements_return_the_columns_the_pivots_read()
    {
        // Five years, so a company with any history at all has invoices in range.
        var parameters = new Dictionary<string, string>
        {
            ["fromDate"] = DateTime.UtcNow.Date.AddYears(-5).ToString("yyyy-MM-dd"),
            ["toDate"] = ToDate.ToString("yyyy-MM-dd")
        };

        await AssertColumnsAsync(
            ReportService.SalesSummaryQueryCode, "Report Sales Summary", ReportService.SalesSummarySql,
            parameters, required: true, "DocCur", "InvoiceCount", "TotalSales", "TotalVat");

        await AssertColumnsAsync(
            ReportService.SalesCustomerCountQueryCode, "Report Sales Unique Customers", ReportService.SalesCustomerCountSql,
            parameters, required: true, "UniqueCustomers");

        await AssertColumnsAsync(
            ReportService.DailySalesQueryCode, "Report Daily Sales", ReportService.DailySalesSql,
            parameters, required: true, "DocDate", "DocCur", "InvoiceCount", "TotalSales");

        await AssertColumnsAsync(
            ReportService.CustomerInvoiceQueryCode, "Report Customer Invoices", ReportService.CustomerInvoiceSql,
            parameters, required: true, "CardCode", "CardName", "DocCur", "InvoiceCount", "TotalPurchases");

        await AssertColumnsAsync(
            ReportService.TopProductsQueryCode, "Report Top Products", ReportService.TopProductsSql,
            parameters, required: true, "ItemCode", "ItemName", "DocCur", "TotalQuantitySold", "TotalRevenue", "TimesOrdered");

        // Aging is asked over the 365 days the report itself uses, not the five years above. It
        // groups by date as well as customer and currency, so its row count grows with the window
        // far faster than the others' — over five years it has run past the client's two-minute
        // timeout. Nothing guarantees a test company has an unpaid invoice either, so this one only
        // checks the shape of whatever comes back.
        await AssertColumnsAsync(
            ReportService.ReceivablesAgingQueryCode, "Report Receivables Aging", ReportService.ReceivablesAgingSql,
            new Dictionary<string, string>
            {
                ["fromDate"] = DateTime.UtcNow.Date.AddDays(-365).ToString("yyyy-MM-dd"),
                ["toDate"] = ToDate.ToString("yyyy-MM-dd")
            },
            required: false, "CardCode", "CardName", "DocDate", "DocCur", "InvoiceCount", "DocTotal", "PaidToDate");
    }

    /// <summary>
    /// The aging buckets and the daily series both read a date out of a SQL row, and SQLQueries
    /// renders a DATE as yyyyMMdd rather than ISO. Parsed by anything general that string is
    /// rejected outright, which silently dates every row to <see cref="DateTime.MinValue"/> — an
    /// invoice bucketed as 90+ regardless of its age, and a daily series that drops every point.
    /// </summary>
    [SapFact]
    public async Task Sql_rows_carry_dates_in_the_compact_form_the_reports_parse()
    {
        var rows = await fixture.Client.ExecuteParameterisedSqlQueryAsync(
            ReportService.DailySalesQueryCode,
            "Report Daily Sales",
            ReportService.DailySalesSql,
            new Dictionary<string, string>
            {
                ["fromDate"] = DateTime.UtcNow.Date.AddYears(-5).ToString("yyyy-MM-dd"),
                ["toDate"] = ToDate.ToString("yyyy-MM-dd")
            });

        Assert.True(rows.Count > 0, "No invoices in the last five years, so the date format cannot be observed.");

        var docDate = rows[0]["DocDate"]?.ToString();
        output.WriteLine($"DocDate as returned: {docDate}");
        Assert.True(
            DateTime.TryParseExact(docDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            $"DocDate '{docDate}' is not the yyyyMMdd the reports parse.");
    }

    /// <summary>
    /// Why these reports are shaped the way they are. Each of these is ordinary HANA that SAP's
    /// validator refuses when the query object is created, and between them they account for every
    /// awkward thing about the statements above: the currency split, the ranking, and the aging
    /// report's two separate sums.
    /// </summary>
    /// <remarks>
    /// Safe to run repeatedly: the create is what fails, so nothing is ever left behind under this
    /// code. A failure here means SAP has started accepting the construct — the report could be
    /// simplified — not that anything is broken.
    /// </remarks>
    [SapTheory]
    [InlineData(
        "case expression",
        """SELECT SUM(CASE WHEN T0."DocCur" = 'USD' THEN 1 ELSE 0 END) AS "C" FROM OINV T0""")]
    [InlineData(
        "subtraction inside an aggregate",
        """SELECT SUM(T0."DocTotal" - T0."PaidToDate") AS "C" FROM OINV T0""")]
    [InlineData(
        "subtraction between aggregates",
        """SELECT SUM(T0."DocTotal") - SUM(T0."PaidToDate") AS "C" FROM OINV T0""")]
    [InlineData(
        "arithmetic in a WHERE clause",
        """SELECT COUNT(T0."DocEntry") AS "C" FROM OINV T0 WHERE (T0."DocTotal" - T0."PaidToDate") > 0.01""")]
    [InlineData(
        "ordering by an aggregate",
        """SELECT T0."DocCur" AS "DocCur", SUM(T0."DocTotal") AS "C" FROM OINV T0 GROUP BY T0."DocCur" ORDER BY SUM(T0."DocTotal") DESC""")]
    public async Task The_validator_still_rejects_the_constructs_these_reports_work_around(string construct, string sql)
    {
        var rejection = await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.Client.ExecuteRawSqlQueryAsync("RPT_REJECTED_PROBE", "Report validator probe", sql));

        output.WriteLine($"{construct}: {rejection.Message}");
    }

    /// <summary>
    /// The reports must not grow OUQR, however many ranges they are asked for. Interpolated dates
    /// gave every request its own permanent row.
    /// </summary>
    [SapFact]
    public async Task Repeated_report_runs_add_no_sap_query_objects()
    {
        // Provision first, so the baseline is taken with every object already in place.
        await RunEveryReportAsync(FromDate, ToDate);

        var before = await CountSqlQueriesAsync();
        output.WriteLine($"SQLQueries before: {before}");

        for (var i = 1; i <= 4; i++)
        {
            await RunEveryReportAsync(FromDate.AddDays(-i * 10), ToDate.AddDays(-i));
        }

        var after = await CountSqlQueriesAsync();
        output.WriteLine($"SQLQueries after 4 further report runs: {after}");

        Assert.Equal(before, after);
    }

    private async Task RunEveryReportAsync(DateTime fromDate, DateTime toDate)
    {
        var reports = BuildReportService();

        await reports.GetSalesSummaryAsync(fromDate, toDate);
        await reports.GetTopProductsAsync(fromDate, toDate, topCount: 10);
        await reports.GetTopCustomersAsync(fromDate, toDate, topCount: 10);
        await reports.GetReceivablesAgingAsync();
    }

    private async Task AssertColumnsAsync(
        string queryCode,
        string queryName,
        string sqlText,
        Dictionary<string, string> parameters,
        bool required,
        params string[] columns)
    {
        var rows = await fixture.Client.ExecuteParameterisedSqlQueryAsync(queryCode, queryName, sqlText, parameters);
        output.WriteLine($"{queryCode}: {rows.Count} rows");

        if (rows.Count == 0)
        {
            Assert.False(required, $"{queryCode} returned no rows. Point these tests at a company with invoice history.");
            return;
        }

        foreach (var column in columns)
        {
            Assert.True(rows[0].ContainsKey(column), $"{queryCode} did not return column '{column}'");
        }
    }

    /// <summary>
    /// A report service over the live client. Its own cache, so each test measures real SAP calls
    /// rather than another test's cached rows.
    /// </summary>
    private ReportService BuildReportService() =>
        new(fixture.Client, new MemoryCache(new MemoryCacheOptions()), NullLogger<ReportService>.Instance);

    /// <summary>
    /// OUQR is not readable through SQLQueries ("Table 'OUQR' not accessible"), so count the entity
    /// set. This runs no SQL and so cannot perturb what it measures.
    /// </summary>
    private async Task<int> CountSqlQueriesAsync()
    {
        var codes = await fixture.Client.GetSqlQueryCodesAsync();
        return codes.Count;
    }
}
