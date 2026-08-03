using System.Diagnostics;
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
/// <b>Not every report is covered here, and the gap is deliberate.</b> The validator also rejects
/// <c>CASE</c> outright — <c>701, "Cannot support the case expression"</c>, confirmed against the
/// live Service Layer on 2026-08-03 for a bare <c>SUM(CASE WHEN … END)</c> with no parameters in
/// sight. Five report statements split currencies with a <c>CASE</c> and so cannot be created at
/// all: sales summary, daily sales, customer invoices, receivables aging and top products. They are
/// parameterised like the rest, so they stop leaking the moment that blocker is lifted, but SAP
/// cannot run them today and a test that asked it to would fail for a reason this change did not
/// cause and does not fix. What is exercised below is every report statement SAP will accept.
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

    [SapFact]
    public async Task Every_report_statement_sap_accepts_is_accepted()
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
    }

    /// <summary>
    /// The point of the whole change: report views must not add SAP query objects, however many
    /// different date ranges they are asked for.
    /// </summary>
    [SapFact]
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
