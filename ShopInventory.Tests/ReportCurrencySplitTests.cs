using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Holds the five currency-split reports to SQL that SAP will actually provision, and pins the
/// arithmetic that moving the split out of SQL had to preserve.
/// </summary>
/// <remarks>
/// All five used to compute their USD/ZiG columns as <c>SUM(CASE WHEN "DocCur" = 'USD' ...)</c>.
/// SAP's SQLQueries validator rejects a CASE expression when the query object is created — 701,
/// "Cannot support the case expression" — so not one of these five objects had ever existed and
/// every one of the reports returned HTTP 400 before reading a row. Nothing downstream could tell:
/// the SQL is valid HANA, and only the create call ever saw the error.
///
/// Grouping by currency instead and folding the rows together in C# is arithmetically identical for
/// the sums, but not for three of the figures, and those three are what most of this file is about:
/// a distinct customer count cannot be added up across currency groups, a top-N cannot be taken
/// before the fold, and an aging bucket has to be derived from the invoice date once the CASE that
/// derived it is gone.
/// </remarks>
public class ReportCurrencySplitTests
{
    private static readonly DateTime From = new(2026, 1, 1);
    private static readonly DateTime To = new(2026, 3, 31);

    /// <summary>
    /// The constructs SAP's validator rejects, each confirmed against the live Service Layer.
    /// Reintroducing any one of them un-provisions the query and 400s the report.
    /// </summary>
    [Theory]
    [InlineData("CASE")]
    [InlineData("COALESCE")]
    [InlineData("TO_DATE")]
    [InlineData("||")]
    public async Task Report_sql_avoids_every_construct_the_validator_rejects(string rejected)
    {
        foreach (var call in await RunEveryReportAsync())
        {
            Assert.DoesNotContain(rejected, call.SqlText, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The validator refuses to order by an aggregate — <c>ORDER BY SUM(...) DESC</c> comes back 701
    /// "Incorrect syntax near 'ORDER'". Ordering a report by its own totals has to happen in C#.
    /// </summary>
    [Fact]
    public async Task Report_sql_never_orders_by_an_aggregate()
    {
        foreach (var call in await RunEveryReportAsync())
        {
            var start = call.SqlText.IndexOf("ORDER BY", StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var orderBy = call.SqlText[start..];
            Assert.DoesNotContain("SUM(", orderBy, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("COUNT(", orderBy, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Report_sql_binds_its_dates_instead_of_interpolating_them()
    {
        var calls = await RunEveryReportAsync();

        Assert.NotEmpty(calls);
        foreach (var call in calls)
        {
            Assert.Contains(":fromDate", call.SqlText, StringComparison.Ordinal);
            Assert.Contains(":toDate", call.SqlText, StringComparison.Ordinal);
            Assert.DoesNotContain("2026-", call.SqlText, StringComparison.Ordinal);
        }

        foreach (var call in await RunRangeReportsAsync(From, To))
        {
            Assert.Equal("2026-01-01", call.Parameters["fromDate"]);
            Assert.Equal("2026-03-31", call.Parameters["toDate"]);
        }
    }

    /// <summary>
    /// The point of the fixed codes: a report run must not add SAP query objects, whatever range it
    /// is asked for. Interpolated dates gave every request its own permanent OUQR row.
    /// </summary>
    [Fact]
    public async Task Report_sql_is_identical_across_date_ranges()
    {
        var first = await RunEveryReportAsync(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));
        var second = await RunEveryReportAsync(new DateTime(2024, 7, 15), new DateTime(2025, 2, 2));

        Assert.Equal(
            first.Select(call => (call.QueryCode, call.SqlText)).Distinct(),
            second.Select(call => (call.QueryCode, call.SqlText)).Distinct());
    }

    [Fact]
    public async Task A_warehouse_filter_gets_its_own_query_code_and_stays_a_bound_value()
    {
        var calls = new List<SqlCall>();
        var service = BuildService(calls, _ => []);

        await service.GetTopProductsAsync(From, To, 10, "WH01");

        var call = Assert.Single(calls);
        Assert.Equal(ReportService.TopProductsByWarehouseQueryCode, call.QueryCode);
        Assert.Equal("WH01", call.Parameters["warehouseCode"]);
        Assert.DoesNotContain("WH01", call.SqlText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sales_summary_splits_the_currency_rows_and_totals_all_of_them()
    {
        var report = await RunSalesSummaryAsync(
        [
            CurrencyRow("USD", invoices: 3, total: 300m, vat: 30m),
            CurrencyRow("ZiG", invoices: 2, total: 500m, vat: 50m),
            // An unset DocCur counted as USD under the old SQL predicate and still has to.
            CurrencyRow(null, invoices: 1, total: 100m, vat: 10m),
            // A currency the split does not name still belongs in the invoice total.
            CurrencyRow("EUR", invoices: 4, total: 700m, vat: 70m)
        ]);

        Assert.Equal(400m, report.TotalSalesUSD);
        Assert.Equal(500m, report.TotalSalesZIG);
        Assert.Equal(40m, report.TotalVatUSD);
        Assert.Equal(50m, report.TotalVatZIG);
        Assert.Equal(10, report.TotalInvoices);
        Assert.Equal(100m, report.AverageInvoiceValueUSD);
        Assert.Equal(250m, report.AverageInvoiceValueZIG);
    }

    /// <summary>
    /// Unique customers is the one figure that cannot come from the currency-grouped statement: a
    /// customer trading in both currencies is in both groups, so adding the groups up counts them
    /// twice. It runs as its own ungrouped COUNT(DISTINCT), which SAP does accept.
    /// </summary>
    [Fact]
    public async Task Unique_customers_is_counted_once_across_currencies_not_summed_per_currency()
    {
        var report = await RunSalesSummaryAsync(
            [
                CurrencyRow("USD", invoices: 5, total: 500m, vat: 0m),
                CurrencyRow("ZiG", invoices: 5, total: 500m, vat: 0m)
            ],
            uniqueCustomers: 7);

        Assert.Equal(7, report.UniqueCustomers);
    }

    [Fact]
    public async Task Daily_sales_folds_a_days_currency_rows_into_one_point()
    {
        // SQLQueries renders a DATE as yyyyMMdd, which general parsing rejects outright. Read wrong,
        // every row here lands on DateTime.MinValue and is dropped by the series' own filter.
        var report = await RunSalesSummaryAsync(
            [],
            dailyRows:
            [
                DailyRow("20260115", "USD", invoices: 2, total: 200m),
                DailyRow("20260115", "ZiG", invoices: 1, total: 400m),
                DailyRow("20260116", "USD", invoices: 3, total: 300m)
            ]);

        Assert.Equal(2, report.DailySales.Count);

        var first = report.DailySales[0];
        Assert.Equal(new DateTime(2026, 1, 15), first.Date);
        Assert.Equal(3, first.InvoiceCount);
        Assert.Equal(200m, first.TotalSalesUSD);
        Assert.Equal(400m, first.TotalSalesZIG);

        Assert.Equal(new DateTime(2026, 1, 16), report.DailySales[1].Date);
    }

    /// <summary>
    /// The limit has to be applied after the fold. Taken before it, the statement ranks item-currency
    /// pairs, so an item whose sales are split across two currencies is ranked on half its quantity
    /// and can be dropped by a smaller item that only trades in one.
    /// </summary>
    [Fact]
    public async Task Top_products_ranks_items_after_the_fold_not_item_currency_pairs()
    {
        var calls = new List<SqlCall>();
        var service = BuildService(calls, _ =>
        [
            // 20 in total, but neither row on its own beats SOLO-15.
            ProductRow("SPLIT", "Split across currencies", "USD", quantity: 10m, revenue: 100m, lines: 1),
            ProductRow("SPLIT", "Split across currencies", "ZiG", quantity: 10m, revenue: 900m, lines: 1),
            ProductRow("SOLO-15", "Single currency", "USD", quantity: 15m, revenue: 150m, lines: 3),
            ProductRow("SOLO-12", "Also single", "USD", quantity: 12m, revenue: 120m, lines: 2)
        ]);

        var report = await service.GetTopProductsAsync(From, To, topCount: 2);

        Assert.Equal(["SPLIT", "SOLO-15"], report.TopProducts.Select(p => p.ItemCode));

        var split = report.TopProducts[0];
        Assert.Equal(1, split.Rank);
        Assert.Equal(20m, split.TotalQuantitySold);
        Assert.Equal(100m, split.TotalRevenueUSD);
        Assert.Equal(900m, split.TotalRevenueZIG);
        Assert.Equal(2, split.TimesOrdered);
        Assert.Equal("Split across currencies", split.ItemName);

        // The statement fetches the range unlimited now, so the limit must not reach SAP.
        Assert.DoesNotContain("TOP", Assert.Single(calls).SqlText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Top_customers_folds_a_customers_currency_rows_together()
    {
        var calls = new List<SqlCall>();
        var service = BuildService(calls, _ =>
        [
            CustomerRow("C1", "Acme", "USD", invoices: 2, total: 200m),
            CustomerRow("C1", "Acme", "ZiG", invoices: 3, total: 900m),
            CustomerRow("C2", "Beta", "USD", invoices: 1, total: 50m)
        ]);

        var report = await service.GetTopCustomersAsync(From, To, topCount: 10);

        Assert.Equal(2, report.TotalCustomers);

        var acme = report.TopCustomers[0];
        Assert.Equal("C1", acme.CardCode);
        Assert.Equal("Acme", acme.CardName);
        Assert.Equal(5, acme.InvoiceCount);
        Assert.Equal(200m, acme.TotalPurchasesUSD);
        Assert.Equal(900m, acme.TotalPurchasesZIG);
    }

    /// <summary>
    /// Van sales accounts book stock onto the vans rather than out to a customer, and they trade at
    /// a scale that took every place in the ranking. They go before the fold, so the customer total
    /// counts the same book of business the ranking is drawn from.
    /// </summary>
    [Fact]
    public async Task Top_customers_leaves_out_the_van_sales_accounts()
    {
        var calls = new List<SqlCall>();
        var service = BuildService(calls, _ =>
        [
            CustomerRow("VAN008", "Van Sales East 2", "USD", invoices: 400, total: 738_200_000m),
            CustomerRow("VAN020", "Van Sales CBD", "USD", invoices: 300, total: 387_600_000m),
            CustomerRow("C1", "Acme", "USD", invoices: 2, total: 200m),
            CustomerRow("C2", "Beta", "USD", invoices: 1, total: 50m)
        ]);

        var report = await service.GetTopCustomersAsync(From, To, topCount: 10);

        Assert.Equal(["C1", "C2"], report.TopCustomers.Select(c => c.CardCode));
        Assert.Equal(2, report.TotalCustomers);

        // The ranking is renumbered around them rather than carrying their gaps.
        Assert.Equal([1, 2], report.TopCustomers.Select(c => c.Rank));
    }

    /// <summary>
    /// The buckets were a second CASE, over boundaries that move daily. Deriving them from the raw
    /// invoice date is what lets the statement stay constant.
    /// </summary>
    [Fact]
    public async Task Receivables_aging_buckets_are_derived_from_the_invoice_date()
    {
        var today = DateTime.UtcNow.Date;
        var calls = new List<SqlCall>();
        var service = BuildService(calls, _ =>
        [
            AgingRow("C1", "Acme", today.AddDays(-5), "USD", invoices: 1, outstanding: 10m),
            // The boundary itself is inclusive, exactly as ">= today - 30" was.
            AgingRow("C1", "Acme", today.AddDays(-30), "USD", invoices: 1, outstanding: 20m),
            AgingRow("C1", "Acme", today.AddDays(-31), "USD", invoices: 1, outstanding: 40m),
            AgingRow("C2", "Beta", today.AddDays(-75), "USD", invoices: 2, outstanding: 80m),
            AgingRow("C2", "Beta", today.AddDays(-200), "ZiG", invoices: 1, outstanding: 160m),
            // Sub-cent dust. The statement's open-item filter can only compare the two columns now,
            // so it lets this through and the report has to drop it.
            AgingRow("C3", "Rounding", today.AddDays(-5), "USD", invoices: 1, outstanding: 0.004m)
        ]);

        var report = await service.GetReceivablesAgingAsync();

        Assert.Equal(30m, report.Current.AmountUSD);
        Assert.Equal(2, report.Current.InvoiceCount);
        Assert.Equal(40m, report.Days31To60.AmountUSD);
        Assert.Equal(80m, report.Days61To90.AmountUSD);
        Assert.Equal(160m, report.Over90Days.AmountZIG);
        Assert.Equal(0m, report.Over90Days.AmountUSD);

        Assert.Equal(150m, report.TotalOutstandingUSD);
        Assert.Equal(160m, report.TotalOutstandingZIG);

        var acme = report.CustomerAging.Single(c => c.CardCode == "C1");
        Assert.Equal(30m, acme.CurrentUSD);
        Assert.Equal(40m, acme.Days31To60USD);
        Assert.Equal(3, acme.TotalInvoices);

        Assert.DoesNotContain(report.CustomerAging, c => c.CardCode == "C3");
    }

    private sealed record SqlCall(string QueryCode, string SqlText, IReadOnlyDictionary<string, string> Parameters);

    private static Dictionary<string, object?> CurrencyRow(string? currency, int invoices, decimal total, decimal vat) =>
        new()
        {
            ["DocCur"] = currency,
            ["InvoiceCount"] = invoices,
            ["TotalSales"] = total,
            ["TotalVat"] = vat
        };

    private static Dictionary<string, object?> DailyRow(string docDate, string currency, int invoices, decimal total) =>
        new()
        {
            ["DocDate"] = docDate,
            ["DocCur"] = currency,
            ["InvoiceCount"] = invoices,
            ["TotalSales"] = total
        };

    private static Dictionary<string, object?> ProductRow(
        string itemCode, string itemName, string currency, decimal quantity, decimal revenue, int lines) =>
        new()
        {
            ["ItemCode"] = itemCode,
            ["ItemName"] = itemName,
            ["DocCur"] = currency,
            ["TotalQuantitySold"] = quantity,
            ["TotalRevenue"] = revenue,
            ["TimesOrdered"] = lines
        };

    private static Dictionary<string, object?> CustomerRow(
        string cardCode, string cardName, string currency, int invoices, decimal total) =>
        new()
        {
            ["CardCode"] = cardCode,
            ["CardName"] = cardName,
            ["DocCur"] = currency,
            ["InvoiceCount"] = invoices,
            ["TotalPurchases"] = total
        };

    /// <summary>
    /// The statement cannot subtract, so it returns both sums and the report does it on read.
    /// </summary>
    private static Dictionary<string, object?> AgingRow(
        string cardCode, string cardName, DateTime docDate, string currency, int invoices, decimal outstanding) =>
        new()
        {
            ["CardCode"] = cardCode,
            ["CardName"] = cardName,
            ["DocDate"] = docDate.ToString("yyyyMMdd"),
            ["DocCur"] = currency,
            ["InvoiceCount"] = invoices,
            ["DocTotal"] = outstanding + 1000m,
            ["PaidToDate"] = 1000m
        };

    private static async Task<SalesSummaryReportDto> RunSalesSummaryAsync(
        List<Dictionary<string, object?>> summaryRows,
        int uniqueCustomers = 0,
        List<Dictionary<string, object?>>? dailyRows = null)
    {
        var service = BuildService([], queryCode => queryCode switch
        {
            ReportService.SalesSummaryQueryCode => summaryRows,
            ReportService.DailySalesQueryCode => dailyRows ?? [],
            ReportService.SalesCustomerCountQueryCode =>
                [new Dictionary<string, object?> { ["UniqueCustomers"] = uniqueCustomers }],
            _ => []
        });

        return await service.GetSalesSummaryAsync(From, To);
    }

    /// <summary>
    /// Every report that carries a currency split, so a construct the validator rejects cannot hide
    /// in whichever one a test forgot. Aging is included but reports its own range, taken off the
    /// clock rather than from a caller — see <see cref="RunRangeReportsAsync"/>.
    /// </summary>
    private static async Task<List<SqlCall>> RunEveryReportAsync(DateTime? fromDate = null, DateTime? toDate = null)
    {
        var calls = await RunRangeReportsAsync(fromDate ?? From, toDate ?? To);

        var agingCalls = new List<SqlCall>();
        await BuildService(agingCalls, _ => []).GetReceivablesAgingAsync();
        calls.AddRange(agingCalls);

        // The five reports, plus the unique-customer count and the warehouse-filtered variant.
        Assert.Equal(7, calls.Select(call => call.QueryCode).Distinct().Count());
        return calls;
    }

    /// <summary>The reports whose statements bind the range the caller asked for.</summary>
    private static async Task<List<SqlCall>> RunRangeReportsAsync(DateTime fromDate, DateTime toDate)
    {
        var calls = new List<SqlCall>();
        var service = BuildService(calls, _ => []);

        await service.GetSalesSummaryAsync(fromDate, toDate);
        await service.GetTopProductsAsync(fromDate, toDate, 10);
        await service.GetTopProductsAsync(fromDate, toDate, 10, "WH01");
        await service.GetTopCustomersAsync(fromDate, toDate, 10);

        return calls;
    }

    private static ReportService BuildService(
        List<SqlCall> calls,
        Func<string, List<Dictionary<string, object?>>> rowsFor)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync) =>
                RecordAndAnswer(calls, rowsFor, args!),
            nameof(ISAPServiceLayerClient.GetIncomingPaymentsByDateRangeAsync) =>
                Task.FromResult(new List<IncomingPayment>()),
            _ => throw new InvalidOperationException($"unexpected call {method.Name}")
        });

        return new ReportService(
            sap,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ReportService>.Instance);
    }

    private static object RecordAndAnswer(
        List<SqlCall> calls,
        Func<string, List<Dictionary<string, object?>>> rowsFor,
        object?[] args)
    {
        var queryCode = (string)args[0]!;
        calls.Add(new SqlCall(
            queryCode,
            (string)args[2]!,
            (IReadOnlyDictionary<string, string>)args[3]!));

        return Task.FromResult(rowsFor(queryCode));
    }
}
