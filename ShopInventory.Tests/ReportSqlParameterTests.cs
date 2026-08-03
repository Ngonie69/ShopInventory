using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Holds the report queries to a fixed set of SAP query objects.
/// </summary>
/// <remarks>
/// Every date-ranged report statement used to interpolate its from/to dates straight into the SQL
/// and run it through the content-addressed executor. That executor only reuses a SAP-side object
/// when the text is byte-identical, and report text never was: the range moves with whatever the
/// user picked, and the receivables aging buckets moved again every midnight. So each report view
/// created permanent OUQR rows that were never reused and, at OUQR's size, could not practically be
/// deleted — the leak removed everywhere else, still running here.
///
/// What makes it worth a test rather than a comment is that nothing about it is visible from
/// outside: the interpolated version returned exactly the right numbers. Only the SAP-side row
/// count moved.
/// </remarks>
public class ReportSqlParameterTests
{
    private static readonly DateTime From = new(2026, 1, 9);
    private static readonly DateTime To = new(2026, 3, 4);

    [Fact]
    public async Task Report_sql_carries_no_interpolated_dates()
    {
        var calls = await RunEveryDateRangedReportAsync(From, To);

        Assert.NotEmpty(calls);
        foreach (var call in calls)
        {
            Assert.DoesNotContain("2026-", call.SqlText, StringComparison.Ordinal);
            Assert.Contains(":fromDate", call.SqlText, StringComparison.Ordinal);
            Assert.Contains(":toDate", call.SqlText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Report_sql_is_identical_across_date_ranges()
    {
        var first = await RunEveryDateRangedReportAsync(From, To);
        var second = await RunEveryDateRangedReportAsync(new DateTime(2025, 7, 15), new DateTime(2025, 8, 2));

        // Same codes and the same statements, so SAP holds one object per report forever.
        Assert.Equal(
            first.Select(call => (call.QueryCode, call.SqlText)),
            second.Select(call => (call.QueryCode, call.SqlText)));
    }

    [Fact]
    public async Task Dates_are_bound_as_yyyy_MM_dd()
    {
        var calls = await RunEveryDateRangedReportAsync(From, To);

        foreach (var call in calls)
        {
            Assert.Equal("2026-01-09", call.Parameters["fromDate"]);
            Assert.Equal("2026-03-04", call.Parameters["toDate"]);
        }
    }

    /// <summary>
    /// The order fulfillment report's four statements, named because they are the ones that were
    /// still leaking after every other caller had been converted.
    /// </summary>
    [Fact]
    public async Task Order_fulfillment_runs_four_statements_under_fixed_codes()
    {
        var calls = await RunReportAsync(reports => reports.GetOrderFulfillmentAsync(From, To));

        Assert.Equal(
            [
                ReportService.OrderFulfillmentCustomerQueryCode,
                ReportService.OrderFulfillmentDailyQueryCode,
                ReportService.OrderFulfillmentLineQueryCode,
                ReportService.OrderDirectInvoiceQueryCode
            ],
            calls.Select(call => call.QueryCode));
    }

    /// <summary>
    /// SAP's SQLQueries validator rejects COALESCE at create with 701, which took the whole sales
    /// order vs invoice report down before a row was read. The row readers already map a missing or
    /// null cell to 0, so nothing needs it.
    /// </summary>
    [Fact]
    public async Task Report_sql_uses_no_function_the_sap_validator_rejects()
    {
        var calls = await RunEveryDateRangedReportAsync(From, To);

        foreach (var call in calls)
        {
            foreach (var rejected in new[] { "COALESCE", "TO_DATE", "||" })
            {
                Assert.DoesNotContain(rejected, call.SqlText, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The aging buckets roll with today's date, so interpolating them minted a fresh object every
    /// midnight on top of one per range. They have to be bound like the range itself.
    /// </summary>
    [Fact]
    public async Task Receivables_aging_binds_its_rolling_bucket_boundaries()
    {
        var call = Assert.Single(await RunReportAsync(reports => reports.GetReceivablesAgingAsync()));

        Assert.Equal(ReportService.ReceivablesAgingQueryCode, call.QueryCode);
        Assert.Contains(":currentBoundary", call.SqlText, StringComparison.Ordinal);
        Assert.Contains(":days31Boundary", call.SqlText, StringComparison.Ordinal);
        Assert.Contains(":days61Boundary", call.SqlText, StringComparison.Ordinal);

        Assert.Equal(
            DateTime.UtcNow.Date.AddDays(-30).ToString("yyyy-MM-dd"),
            call.Parameters["currentBoundary"]);
        Assert.Equal(
            DateTime.UtcNow.Date.AddDays(-60).ToString("yyyy-MM-dd"),
            call.Parameters["days31Boundary"]);
        Assert.Equal(
            DateTime.UtcNow.Date.AddDays(-90).ToString("yyyy-MM-dd"),
            call.Parameters["days61Boundary"]);
    }

    /// <summary>
    /// Top products is the one report whose shape varies — neither the TOP count nor the optional
    /// warehouse filter can be a bound parameter — so it keeps a content-addressed code. What must
    /// not vary is the text across date ranges.
    /// </summary>
    [Fact]
    public async Task Top_products_varies_by_shape_but_not_by_date_range()
    {
        var ten = await RunReportAsync(reports => reports.GetTopProductsAsync(From, To, 10));
        var tenElsewhere = await RunReportAsync(reports =>
            reports.GetTopProductsAsync(new DateTime(2024, 2, 2), new DateTime(2024, 4, 4), 10));
        var fifty = await RunReportAsync(reports => reports.GetTopProductsAsync(From, To, 50));

        Assert.Equal(ten.Single().SqlText, tenElsewhere.Single().SqlText);
        Assert.NotEqual(ten.Single().SqlText, fifty.Single().SqlText);
        Assert.Equal(ReportService.TopProductsQueryCodePrefix, ten.Single().QueryCode);
    }

    private sealed record SqlCall(string QueryCode, string SqlText, IReadOnlyDictionary<string, string> Parameters);

    /// <summary>
    /// Every report that puts a date range into SQL, in one pass, so a new one cannot be added
    /// without these assertions seeing it.
    /// </summary>
    private static async Task<List<SqlCall>> RunEveryDateRangedReportAsync(DateTime fromDate, DateTime toDate) =>
        await RunReportAsync(async reports =>
        {
            await reports.GetSalesSummaryAsync(fromDate, toDate);
            await reports.GetTopProductsAsync(fromDate, toDate);
            await reports.GetSlowMovingProductsAsync(fromDate, toDate);
            await reports.GetTopCustomersAsync(fromDate, toDate);
            await reports.GetOrderFulfillmentAsync(fromDate, toDate);
        });

    private static async Task<List<SqlCall>> RunReportAsync(Func<IReportService, Task> run)
    {
        var calls = new List<SqlCall>();

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync) or
            nameof(ISAPServiceLayerClient.ExecuteScopedParameterisedSqlQueryAsync) =>
                RecordAndAnswer(calls, args!),

            // The stocked-items statement takes no values at all, so it never carried a date and is
            // not part of what these tests police.
            nameof(ISAPServiceLayerClient.ExecuteScopedRawSqlQueryAsync) =>
                Task.FromResult(new List<Dictionary<string, object?>>()),

            nameof(ISAPServiceLayerClient.GetIncomingPaymentsByDateRangeAsync) =>
                Task.FromResult(new List<IncomingPayment>()),

            _ => throw new InvalidOperationException($"unexpected call {method.Name}")
        });

        await run(new ReportService(
            sap,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ReportService>.Instance));

        return calls;
    }

    private static object RecordAndAnswer(List<SqlCall> calls, object?[] args)
    {
        calls.Add(new SqlCall(
            (string)args[0]!,
            (string)args[2]!,
            (IReadOnlyDictionary<string, string>)args[3]!));

        return Task.FromResult(new List<Dictionary<string, object?>>());
    }
}
