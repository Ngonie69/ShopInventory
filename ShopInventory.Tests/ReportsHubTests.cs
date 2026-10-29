using System.Security.Claims;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Components;
using ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using LowStockItem = ShopInventory.Web.Models.LowStockItem;
using WebReportService = ShopInventory.Web.Services.IReportService;

namespace ShopInventory.Tests;

/// <summary>
/// The Reports Hub: which dates a period means, what the "Needs attention" list says, which
/// reports a role is offered, how the loaders behave when part of SAP does not answer, and the
/// workbook its Export button builds.
/// </summary>
public class ReportsHubTests
{
    // A Thursday, so "this week" starts on the Monday three days before.
    private static readonly DateOnly Today = new(2026, 10, 29);

    // ── Periods ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(ReportsPeriodPreset.Today, "2026-10-29", "2026-10-29", "2026-10-22", "2026-10-22")]
    [InlineData(ReportsPeriodPreset.Yesterday, "2026-10-28", "2026-10-28", "2026-10-21", "2026-10-21")]
    [InlineData(ReportsPeriodPreset.ThisWeek, "2026-10-26", "2026-10-29", "2026-10-19", "2026-10-22")]
    [InlineData(ReportsPeriodPreset.ThisMonth, "2026-10-01", "2026-10-29", "2026-09-01", "2026-09-29")]
    [InlineData(ReportsPeriodPreset.Last30Days, "2026-09-30", "2026-10-29", "2026-08-31", "2026-09-29")]
    public void A_preset_covers_its_days_and_compares_with_the_matching_days_before(
        ReportsPeriodPreset preset, string from, string to, string previousFrom, string previousTo)
    {
        var period = ReportsPeriod.Resolve(preset, Today);

        Assert.Equal(DateOnly.Parse(from), period.From);
        Assert.Equal(DateOnly.Parse(to), period.To);
        Assert.Equal(DateOnly.Parse(previousFrom), period.PreviousFrom);
        Assert.Equal(DateOnly.Parse(previousTo), period.PreviousTo);
    }

    [Fact]
    public void This_month_on_the_31st_is_compared_with_the_whole_of_a_shorter_month()
    {
        var period = ReportsPeriod.Resolve(ReportsPeriodPreset.ThisMonth, new DateOnly(2026, 10, 31));

        Assert.Equal(new DateOnly(2026, 9, 1), period.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 9, 30), period.PreviousTo);
    }

    [Fact]
    public void A_custom_range_is_put_in_order_kept_out_of_the_future_and_compared_with_as_many_days_before()
    {
        var period = ReportsPeriod.Resolve(ReportsPeriodPreset.Custom, Today, new DateOnly(2026, 11, 5), new DateOnly(2026, 10, 20));

        Assert.Equal(new DateOnly(2026, 10, 20), period.From);
        Assert.Equal(Today, period.To);
        Assert.Equal(new DateOnly(2026, 10, 10), period.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 10, 19), period.PreviousTo);
        Assert.True(period.EndsToday);
    }

    [Fact]
    public void A_short_period_still_charts_two_weeks()
    {
        Assert.Equal(new DateOnly(2026, 10, 16), ReportsPeriod.Resolve(ReportsPeriodPreset.Today, Today).ChartFrom);
        Assert.Equal(new DateOnly(2026, 10, 1), ReportsPeriod.Resolve(ReportsPeriodPreset.ThisMonth, Today).ChartFrom);
    }

    [Fact]
    public void A_named_period_in_the_address_is_worked_out_afresh_and_only_custom_reads_the_dates()
    {
        var week = ReportsPeriod.FromQuery("week", "2020-01-01", "2020-01-31", Today);
        var custom = ReportsPeriod.FromQuery("custom", "2026-10-01", "2026-10-10", Today);
        var datesOnly = ReportsPeriod.FromQuery(null, "2026-10-01", "2026-10-10", Today);
        var nonsense = ReportsPeriod.FromQuery("fortnight", null, null, Today);

        Assert.Equal(new DateOnly(2026, 10, 26), week.From);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10)), (custom.From, custom.To));
        Assert.Equal(ReportsPeriodPreset.Custom, datesOnly.Preset);
        Assert.Equal(ReportsPeriodPreset.ThisMonth, nonsense.Preset);
    }

    [Fact]
    public void A_period_survives_the_round_trip_through_its_own_address()
    {
        foreach (var preset in Enum.GetValues<ReportsPeriodPreset>())
        {
            var period = ReportsPeriod.Resolve(preset, Today, new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 9));
            var query = System.Web.HttpUtility.ParseQueryString(period.Query);

            Assert.Equal(period, ReportsPeriod.FromQuery(query["period"], query["from"], query["to"], Today));
        }
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-29", true, "1 – 29 Oct 2026")]
    [InlineData("2026-09-30", "2026-10-29", true, "30 Sep – 29 Oct 2026")]
    [InlineData("2026-10-29", "2026-10-29", true, "Thu 29 Oct 2026")]
    [InlineData("2025-12-30", "2026-01-02", false, "30 Dec 2025 – 2 Jan 2026")]
    public void Ranges_read_the_way_people_write_them(string from, string to, bool withYear, string expected)
    {
        Assert.Equal(expected, ReportsPeriod.FormatRange(DateOnly.Parse(from), DateOnly.Parse(to), withYear));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(870, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(1700, 2000)]
    [InlineData(22100, 25000)]
    [InlineData(41000, 50000)]
    public void The_chart_axis_tops_out_at_a_round_figure(decimal max, decimal expected)
    {
        Assert.Equal(expected, ReportsHubOverview.NiceCeiling(max));
    }

    // ── Needs attention ───────────────────────────────────────────────

    private static readonly ReportsPeriod Month = ReportsPeriod.Resolve(ReportsPeriodPreset.ThisMonth, Today);

    [Fact]
    public void Nothing_is_flagged_before_the_reports_arrive_or_when_they_show_nothing_wrong()
    {
        Assert.Empty(ReportsAttention.Build(Month, null, null, null));

        var clean = ReportsAttention.Build(Month,
            new ReportsHeadlineResult(null, null, new ReceivablesAgingReport(), []),
            new ReportsActivityResult(null, null, null, null, new OrderFulfillmentReport(), new CreditNoteSummaryReport { CreditToSalesRatioPercent = 1.2m }, []),
            new ReportsStockWatchResult(new LowStockAlertReport(), new SlowMovingProductsReport(), []));

        Assert.Empty(clean);
    }

    [Fact]
    public void Each_problem_gets_one_line_most_urgent_first_linking_to_its_report_for_the_same_dates()
    {
        var aging = new ReceivablesAgingReport
        {
            TotalOutstandingUSD = 212_480m,
            Over90Days = new AgingBucket { AmountUSD = 38_900m, PercentOfTotal = 18.3m, InvoiceCount = 40 },
            CustomerAging =
            [
                new CustomerAging { CardName = "Small Shop", Over90DaysUSD = 900m },
                new CustomerAging { CardName = "Chitungwiza Cash & Carry", Over90DaysUSD = 15_600m },
                new CustomerAging { CardName = "Paid Up", Over90DaysUSD = 0m },
            ]
        };
        var previous = new ProfitOverviewReport { TotalRevenueUSD = 100_000m, TotalCreditNotesUSD = 2_100m };
        var activity = new ReportsActivityResult(null, null, null, null,
            new OrderFulfillmentReport { OpenOrders = 23, TotalPendingValueUSD = 41_200m },
            new CreditNoteSummaryReport
            {
                CreditToSalesRatioPercent = 3.4m,
                ByCustomer = [new CreditNoteByCustomer { CardName = "Greenfield", TotalAmountUSD = 900m }]
            }, []);
        var stock = new ReportsStockWatchResult(
            new LowStockAlertReport
            {
                CriticalCount = 6,
                WarningCount = 8,
                Items = [new LowStockItem { WarehouseCode = "KEFSHOP" }, new LowStockItem { WarehouseCode = "kefshop" }, new LowStockItem { WarehouseCode = "HRE" }]
            },
            new SlowMovingProductsReport
            {
                Products = [new SlowMovingProduct { ItemName = "Old Cheese", DaysSinceLastSale = 999 }, new SlowMovingProduct { ItemName = "Quiet Butter", DaysSinceLastSale = 75, LastSoldDate = new DateTime(2026, 8, 15) }]
            }, []);

        var items = ReportsAttention.Build(Month, new ReportsHeadlineResult(null, previous, aging, []), activity, stock);

        Assert.Collection(items,
            low =>
            {
                Assert.Equal(ReportsAttentionSeverity.Critical, low.Severity);
                Assert.Equal("6 stock lines critically low", low.Title);
                Assert.Equal("8 more below reorder level · 2 warehouses", low.Detail);
                Assert.Equal("/reports?tab=alerts&period=month&from=2026-10-01&to=2026-10-29", low.Href);
            },
            overdue =>
            {
                Assert.Equal(ReportsAttentionSeverity.Critical, overdue.Severity);
                Assert.Equal("$38,900 owed for more than 90 days", overdue.Title);
                Assert.Equal("2 customers · largest Chitungwiza Cash & Carry ($15,600)", overdue.Detail);
            },
            orders =>
            {
                Assert.Equal(ReportsAttentionSeverity.Watch, orders.Severity);
                Assert.Equal("23 sales orders still open", orders.Title);
                Assert.StartsWith("$41,200 not yet invoiced", orders.Detail);
            },
            credit =>
            {
                Assert.Equal(ReportsAttentionSeverity.Watch, credit.Severity);
                Assert.Equal("Credit notes at 3.4% of sales", credit.Title);
                Assert.Equal("Up from 2.1% the period before · most to Greenfield", credit.Detail);
            },
            slow =>
            {
                Assert.Equal(ReportsAttentionSeverity.Fyi, slow.Severity);
                Assert.Equal("2 products in stock unsold for 60+ days", slow.Title);
                Assert.Equal("1 with no sale in the last 6 months", slow.Detail);
            });
    }

    [Fact]
    public void A_small_overdue_share_is_one_to_watch_and_a_steady_credit_rate_is_not_flagged()
    {
        var aging = new ReceivablesAgingReport { Over90Days = new AgingBucket { AmountUSD = 500m, PercentOfTotal = 2m } };
        var previous = new ProfitOverviewReport { TotalRevenueUSD = 100_000m, TotalCreditNotesUSD = 3_200m };
        var activity = new ReportsActivityResult(null, null, null, null, null,
            new CreditNoteSummaryReport { CreditToSalesRatioPercent = 3.4m }, []);

        var items = ReportsAttention.Build(Month, new ReportsHeadlineResult(null, previous, aging, []), activity, null);

        var only = Assert.Single(items);
        Assert.Equal(ReportsAttentionSeverity.Watch, only.Severity);
        Assert.StartsWith("$500 owed", only.Title);
    }

    // ── Directory ─────────────────────────────────────────────────────

    private static ClaimsPrincipal UserIn(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "someone"), new Claim(ClaimTypes.Role, role)], "test"));

    private static IReadOnlyList<string> Names(IReadOnlyList<ReportsDirectoryGroup> groups) =>
        groups.SelectMany(group => group.Entries).Select(entry => entry.Name).ToList();

    [Fact]
    public void A_role_is_offered_only_the_reports_the_sidebar_shows_it()
    {
        var cashier = Names(ReportsDirectory.For(UserIn("Cashier"), Month));
        var stockController = Names(ReportsDirectory.For(UserIn("StockController"), Month));
        var admin = Names(ReportsDirectory.For(UserIn("Admin"), Month));

        Assert.Contains("Management Sales", cashier);
        Assert.Contains("Route Sales Report", cashier);
        Assert.DoesNotContain("Daily Truck Report", cashier);
        Assert.DoesNotContain("Customer Revenue", cashier);

        Assert.DoesNotContain("Management Sales", stockController);
        Assert.Contains("Low stock alerts", stockController);
        Assert.Contains("Sales Order vs Invoice", stockController);

        Assert.Equal(ReportsDirectory.All(Month).Sum(group => group.Entries.Count), admin.Count);
    }

    [Fact]
    public void Searching_the_directory_matches_every_word_against_name_description_and_heading()
    {
        var admin = UserIn("Admin");

        Assert.Equal(["Receivables aging"], Names(ReportsDirectory.For(admin, Month, "aging")));
        Assert.Equal(["Departure Compliance"], Names(ReportsDirectory.For(admin, Month, "field  on time")));
        Assert.Empty(ReportsDirectory.For(admin, Month, "nothing like this"));
    }

    [Fact]
    public void Every_tab_the_hub_links_to_is_one_the_reports_page_opens()
    {
        var tabs = ReportsDirectory.All(Month)
            .SelectMany(group => group.Entries)
            .Where(entry => entry.Href.StartsWith("/reports?tab=", StringComparison.Ordinal))
            .Select(entry => System.Web.HttpUtility.ParseQueryString(entry.Href.Split('?')[1])["tab"]!);

        Assert.All(tabs, tab => Assert.Contains(tab, ReportsHubLinks.Tabs));
    }

    // ── Loaders ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_part_SAP_does_not_answer_is_named_and_the_rest_still_arrives()
    {
        var reports = new FakeReports
        {
            Profit = (from, _) => from!.Value.Month == 10 ? new ProfitOverviewReport { NetRevenueUSD = 1 } : throw new HttpRequestException("timeout"),
            Aging = () => new ReceivablesAgingReport()
        };

        var result = await new GetReportsHeadlineHandler(reports, NullLogger<GetReportsHeadlineHandler>.Instance)
            .Handle(new GetReportsHeadlineQuery(Month), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(result.Value.Current);
        Assert.Null(result.Value.Previous);
        Assert.Equal(["previous period"], result.Value.Unavailable);
        Assert.Equal([(new DateTime(2026, 10, 1), new DateTime(2026, 10, 29)), (new DateTime(2026, 9, 1), new DateTime(2026, 9, 29))],
            reports.ProfitCalls.OrderByDescending(call => call.From));
    }

    [Fact]
    public async Task When_nothing_answers_the_hub_says_so_rather_than_showing_zeros()
    {
        var reports = new FakeReports { Down = true };

        var headline = await new GetReportsHeadlineHandler(reports, NullLogger<GetReportsHeadlineHandler>.Instance)
            .Handle(new GetReportsHeadlineQuery(Month), CancellationToken.None);
        var activity = await new GetReportsActivityHandler(reports, NullLogger<GetReportsActivityHandler>.Instance)
            .Handle(new GetReportsActivityQuery(Month), CancellationToken.None);
        var stock = await new GetReportsStockWatchHandler(reports, NullLogger<GetReportsStockWatchHandler>.Instance)
            .Handle(new GetReportsStockWatchQuery(Today), CancellationToken.None);

        Assert.True(headline.IsError);
        Assert.True(activity.IsError);
        Assert.True(stock.IsError);
    }

    [Fact]
    public async Task The_daily_chart_reads_two_weeks_even_for_one_day_while_the_cards_read_the_day()
    {
        var reports = new FakeReports();
        var today = ReportsPeriod.Resolve(ReportsPeriodPreset.Today, Today);

        var result = await new GetReportsActivityHandler(reports, NullLogger<GetReportsActivityHandler>.Instance)
            .Handle(new GetReportsActivityQuery(today), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal((new DateTime(2026, 10, 16), new DateTime(2026, 10, 29)), reports.SalesCall);
        Assert.Equal((new DateTime(2026, 10, 29), new DateTime(2026, 10, 29), GetReportsActivityHandler.TopCount), reports.TopCustomersCall);
    }

    [Fact]
    public async Task Leaving_the_page_cancels_the_reads_rather_than_reporting_them_unavailable()
    {
        using var leaving = new CancellationTokenSource();
        var reports = new FakeReports
        {
            Profit = (_, _) =>
            {
                leaving.Cancel();
                leaving.Token.ThrowIfCancellationRequested();
                return null;
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GetReportsHeadlineHandler(reports, NullLogger<GetReportsHeadlineHandler>.Instance)
                .Handle(new GetReportsHeadlineQuery(Month), leaving.Token));
    }

    // ── Export ────────────────────────────────────────────────────────

    [Fact]
    public void The_workbook_holds_the_figures_the_hub_showed_and_leaves_out_what_it_could_not_load()
    {
        var headline = new ReportsHeadlineResult(
            new ProfitOverviewReport { NetRevenueUSD = 465_180m, GrossProfitUSD = 118_940m, GrossMarginPercent = 24.7m, TotalCollectedUSD = 455_020m, TotalInvoices = 1284 },
            new ProfitOverviewReport { NetRevenueUSD = 438_000m, GrossProfitUSD = 110_000m, GrossMarginPercent = 23.9m, TotalCollectedUSD = 441_000m, TotalInvoices = 1200 },
            new ReceivablesAgingReport { TotalOutstandingUSD = 212_480m, Over90Days = new AgingBucket { AmountUSD = 38_900m, PercentOfTotal = 18.3m } },
            []);
        var activity = new ReportsActivityResult(
            new SalesSummaryReport { FromDate = new DateTime(2026, 10, 1), ToDate = new DateTime(2026, 10, 29), DailySales = [new DailySales { Date = new DateTime(2026, 10, 27), InvoiceCount = 51, TotalSalesUSD = 22_100m }] },
            null, null, null, null, null, ["top customers", "top products", "payments", "open orders", "credit notes"]);
        var attention = new[] { new ReportsAttentionItem(ReportsAttentionSeverity.Critical, "$38,900 owed for more than 90 days", "11 customers", "Receivables", "/reports?tab=aging") };

        var bytes = new ReportExportService().ExportReportsHubToExcel(new ReportsHubExport(Month, headline, activity, attention));
        using var workbook = new XLWorkbook(new MemoryStream(bytes));

        Assert.Equal(["Overview", "Daily sales", "Owed by age"], workbook.Worksheets.Select(sheet => sheet.Name));

        var overview = workbook.Worksheet("Overview");
        // The headline card carries the same label above the table; the table row is the last one.
        var netSales = overview.CellsUsed().Last(cell => cell.GetString() == "Net sales (USD)" && cell.Address.ColumnNumber == 1);
        Assert.Equal(465_180m, netSales.CellRight().GetValue<decimal>());
        Assert.Equal(438_000m, netSales.CellRight(2).GetValue<decimal>());
        Assert.Equal(0.062, netSales.CellRight(3).GetValue<double>(), 3);

        var margin = overview.CellsUsed().First(cell => cell.GetString() == "Gross margin (%)");
        Assert.Equal(0.8m, margin.CellRight(3).GetValue<decimal>());

        var critical = overview.CellsUsed().First(cell => cell.GetString() == "Critical");
        Assert.Equal("$38,900 owed for more than 90 days", critical.CellRight().GetString());

        var daily = workbook.Worksheet("Daily sales");
        var day = daily.CellsUsed().First(cell => cell.DataType == XLDataType.DateTime);
        Assert.Equal(new DateTime(2026, 10, 27), day.GetDateTime());
        Assert.Equal(22_100m, day.CellRight(2).GetValue<decimal>());
    }

    [Fact]
    public void An_empty_hub_still_exports_a_readable_overview()
    {
        var bytes = new ReportExportService().ExportReportsHubToExcel(new ReportsHubExport(Month, null, null, []));
        using var workbook = new XLWorkbook(new MemoryStream(bytes));

        var overview = Assert.Single(workbook.Worksheets);
        Assert.Contains(overview.CellsUsed(), cell => cell.GetString() == "Nothing needed attention for this period.");
    }

    private sealed class FakeReports : WebReportService
    {
        /// <summary>Every read throws, as when SAP is unreachable.</summary>
        public bool Down { get; init; }

        private T Answer<T>(T value) => Down ? throw new InvalidOperationException("SAP down") : value;

        public Func<DateTime?, DateTime?, ProfitOverviewReport?> Profit { get; init; } = (_, _) => new ProfitOverviewReport();
        public Func<ReceivablesAgingReport?> Aging { get; init; } = () => new ReceivablesAgingReport();
        public List<(DateTime From, DateTime To)> ProfitCalls { get; } = [];
        public (DateTime?, DateTime?) SalesCall { get; private set; }
        public (DateTime?, DateTime?, int) TopCustomersCall { get; private set; }

        public Task<ProfitOverviewReport?> GetProfitOverviewAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default)
        {
            lock (ProfitCalls)
            {
                ProfitCalls.Add((fromDate!.Value, toDate!.Value));
            }

            return Task.FromResult(Answer(Profit(fromDate, toDate)));
        }

        public Task<ReceivablesAgingReport?> GetReceivablesAgingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Answer(Aging()));

        public Task<SalesSummaryReport?> GetSalesSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default)
        {
            SalesCall = (fromDate, toDate);
            return Task.FromResult<SalesSummaryReport?>(Answer(new SalesSummaryReport()));
        }

        public Task<TopCustomersReport?> GetTopCustomersAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10, CancellationToken cancellationToken = default)
        {
            TopCustomersCall = (fromDate, toDate, topCount);
            return Task.FromResult<TopCustomersReport?>(Answer(new TopCustomersReport()));
        }

        public Task<TopProductsReport?> GetTopProductsAsync(DateTime? fromDate, DateTime? toDate, int topCount = 10, CancellationToken cancellationToken = default) =>
            Task.FromResult<TopProductsReport?>(Answer(new TopProductsReport()));

        public Task<PaymentSummaryReport?> GetPaymentSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default) =>
            Task.FromResult<PaymentSummaryReport?>(Answer(new PaymentSummaryReport()));

        public Task<OrderFulfillmentReport?> GetOrderFulfillmentAsync(DateTime? fromDate, DateTime? toDate, string? cardCode = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrderFulfillmentReport?>(Answer(new OrderFulfillmentReport()));

        public Task<CreditNoteSummaryReport?> GetCreditNoteSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default) =>
            Task.FromResult<CreditNoteSummaryReport?>(Answer(new CreditNoteSummaryReport()));

        public Task<LowStockAlertReport?> GetLowStockAlertsAsync(string? warehouseCode = null, decimal? threshold = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<LowStockAlertReport?>(Answer(new LowStockAlertReport()));

        public Task<SlowMovingProductsReport?> GetSlowMovingProductsAsync(DateTime? fromDate, DateTime? toDate, int daysThreshold = 30, CancellationToken cancellationToken = default) =>
            Task.FromResult<SlowMovingProductsReport?>(Answer(new SlowMovingProductsReport()));

        public Task<StockSummaryReport?> GetStockSummaryAsync(string? warehouseCode = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<StockSummaryReport?>(Answer(new StockSummaryReport()));

        public Task<PurchaseOrderSummaryReport?> GetPurchaseOrderSummaryAsync(DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken = default) =>
            Task.FromResult<PurchaseOrderSummaryReport?>(Answer(new PurchaseOrderSummaryReport()));
    }
}
