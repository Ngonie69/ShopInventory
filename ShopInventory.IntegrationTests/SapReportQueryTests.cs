using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Services;
using Xunit.Abstractions;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Pins the two properties the report SQL has to have against a real Service Layer: SAP accepts
/// every statement, and running them over new date ranges does not grow OUQR.
/// </summary>
/// <remarks>
/// Neither is reachable from a unit test. SAP validates SqlText when the query object is
/// <em>created</em>, and its accepted grammar is much narrower than HANA's — the order-line
/// statement's <c>COALESCE</c> wrappers were rejected with 701 and took the whole sales order vs
/// invoice report down with them, at create, before a row was read. Only the real validator has an
/// opinion about that.
///
/// And the leak is invisible by construction: the interpolated statements returned exactly the
/// right numbers. Only the SAP-side row count moved.
///
/// <para>
/// <b>The five currency-split reports are covered here now.</b> They were excluded while the
/// validator's rejection of <c>CASE</c> left them impossible to create at all — sales summary,
/// daily sales, customer invoices, receivables aging and top products each split USD from ZiG with
/// <c>SUM(CASE WHEN … END)</c>, so every one of them answered HTTP 400 before reading a row. The
/// split now happens in C# off a <c>GROUP BY T0."DocCur"</c>, which leaves real SQL to exercise.
/// <see cref="The_validator_still_rejects_the_constructs_these_reports_work_around"/> keeps the
/// reasons on the record, including two limits that only surfaced here.
/// </para>
///
/// These tests drive <see cref="ReportService"/> itself rather than copies of its SQL. A copy would
/// let the shipped statements drift away from the ones this proves SAP accepts, which is the one
/// thing these tests exist to establish.
/// </remarks>
[Collection("SAP")]
public class SapReportQueryTests(SapClientFixture fixture, ITestOutputHelper output)
{
    // Narrow, so the order-line statement returns a workable number of rows. Reports are exercised
    // for whether SAP accepts and reuses them, not for what the data says.
    private static readonly (DateTime From, DateTime To)[] Ranges =
    [
        (new DateTime(2023, 6, 1), new DateTime(2023, 6, 3)),
        (new DateTime(2023, 7, 5), new DateTime(2023, 7, 6)),
        (new DateTime(2023, 8, 9), new DateTime(2023, 8, 11)),
        (new DateTime(2023, 9, 12), new DateTime(2023, 9, 13))
    ];

    [SapSqlFact]
    public async Task Every_report_statement_is_accepted()
    {
        var reports = CreateReportService();
        var (from, to) = Ranges[0];

        // Four statements, and the order-line one is the statement whose COALESCE wrappers SAP
        // refused. If the validator still objects to anything in it, this throws.
        var fulfillment = await reports.GetOrderFulfillmentAsync(from, to);
        output.WriteLine(
            $"order fulfillment: {fulfillment.TotalOrders} orders, {fulfillment.TotalLineItems} lines, "
            + $"{fulfillment.Orders.Count} order details");

        // Two more: the stocked-item statement, which carries no values at all, and item last sales,
        // which is now bound.
        var slowMoving = await reports.GetSlowMovingProductsAsync(from, to);
        output.WriteLine($"slow moving: {slowMoving.Products.Count} rows");

        // And the five that could not be created at all until the currency split moved to C#.
        var salesSummary = await reports.GetSalesSummaryAsync(from, to);
        output.WriteLine(
            $"sales summary: {salesSummary.TotalInvoices} invoices, {salesSummary.UniqueCustomers} customers, "
            + $"{salesSummary.DailySales.Count} days, USD {salesSummary.TotalSalesUSD}, ZiG {salesSummary.TotalSalesZIG}");

        var topProducts = await reports.GetTopProductsAsync(from, to, topCount: 10);
        output.WriteLine($"top products: {topProducts.TopProducts.Count} items");

        var topCustomers = await reports.GetTopCustomersAsync(from, to, topCount: 10);
        output.WriteLine($"top customers: {topCustomers.TotalCustomers} customers");

        // The widest of them: aging takes its own 365-day range off the clock rather than the caller.
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

        var byWarehouse = await reports.GetTopProductsAsync(from, to, topCount: 10, warehouseCode);
        output.WriteLine($"top products in {warehouseCode}: {byWarehouse.TopProducts.Count} items");
    }

    /// <summary>
    /// Every column the currency pivots read has to come back under the name they read it by. A
    /// renamed alias is not an error anywhere — the row simply has no such key, and the report
    /// quietly reports zero.
    /// </summary>
    [SapSqlFact]
    public async Task The_statements_return_the_columns_the_pivots_read()
    {
        // Five years, so a company with any history at all has invoices in range.
        var parameters = new Dictionary<string, string>
        {
            ["fromDate"] = DateTime.UtcNow.Date.AddYears(-5).ToString("yyyy-MM-dd"),
            ["toDate"] = DateTime.UtcNow.Date.ToString("yyyy-MM-dd")
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
                ["toDate"] = DateTime.UtcNow.Date.ToString("yyyy-MM-dd")
            },
            required: false, "CardCode", "CardName", "DocDate", "DocCur", "InvoiceCount", "DocTotal", "PaidToDate");
    }

    /// <summary>
    /// The aging buckets and the daily series both read a date out of a SQL row, and SQLQueries
    /// renders a DATE as yyyyMMdd rather than ISO. Parsed by anything general that string is
    /// rejected outright, which silently dates every row to <see cref="DateTime.MinValue"/> — an
    /// invoice bucketed as 90+ regardless of its age, and a daily series that drops every point.
    /// </summary>
    [SapSqlFact]
    public async Task Sql_rows_carry_dates_in_the_compact_form_the_reports_parse()
    {
        var rows = await fixture.Client.ExecuteParameterisedSqlQueryAsync(
            ReportService.DailySalesQueryCode,
            "Report Daily Sales",
            ReportService.DailySalesSql,
            new Dictionary<string, string>
            {
                ["fromDate"] = DateTime.UtcNow.Date.AddYears(-5).ToString("yyyy-MM-dd"),
                ["toDate"] = DateTime.UtcNow.Date.ToString("yyyy-MM-dd")
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
    /// awkward thing about the statements: the currency split, the ranking, and the aging report's
    /// two separate sums.
    /// </summary>
    /// <remarks>
    /// Safe to run repeatedly: the create is what fails, so nothing is ever left behind under this
    /// code. A failure here means SAP has started accepting the construct — the report could be
    /// simplified — not that anything is broken.
    /// </remarks>
    [SapSqlTheory]
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
    /// The point of the whole change: report views must not add SAP query objects, however many
    /// different date ranges they are asked for.
    /// </summary>
    [SapSqlFact]
    public async Task Further_date_ranges_add_no_sap_query_objects()
    {
        // One service for the whole run. Its caches are keyed by date range, so every range below
        // still reaches SAP — but the dateless stocked-item statement, which is the slow one and
        // cannot leak because it carries no values, is fetched once instead of four times.
        var reports = CreateReportService();

        // Provision first, so the baseline is taken with every object already in place.
        await RunDateRangedReportsAsync(reports, Ranges[0]);

        var before = await CountSqlQueriesAsync();
        output.WriteLine($"SQLQueries before: {before}");

        foreach (var range in Ranges[1..])
        {
            var elapsed = Stopwatch.StartNew();
            await RunDateRangedReportsAsync(reports, range);
            output.WriteLine($"{range.From:yyyy-MM-dd}..{range.To:yyyy-MM-dd} took {elapsed.Elapsed.TotalSeconds:N1}s");
        }

        var after = await CountSqlQueriesAsync();
        output.WriteLine($"SQLQueries after {Ranges.Length - 1} further date ranges: {after}");

        Assert.Equal(before, after);
    }

    private static async Task RunDateRangedReportsAsync(
        IReportService reports,
        (DateTime From, DateTime To) range)
    {
        await reports.GetOrderFulfillmentAsync(range.From, range.To);
        await reports.GetSlowMovingProductsAsync(range.From, range.To);
        await reports.GetSalesSummaryAsync(range.From, range.To);
        await reports.GetTopProductsAsync(range.From, range.To);
        await reports.GetTopCustomersAsync(range.From, range.To);
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

    private ReportService CreateReportService() =>
        new(fixture.Client, new MemoryCache(new MemoryCacheOptions()), NullLogger<ReportService>.Instance);

    /// <summary>
    /// OUQR itself is not readable through SQLQueries ("Table 'OUQR' not accessible"), so count the
    /// entity set. This runs no SQL and so cannot perturb what it measures.
    /// </summary>
    private async Task<int> CountSqlQueriesAsync()
    {
        var codes = await fixture.Client.GetSqlQueryCodesAsync();
        return codes.Count;
    }
}
