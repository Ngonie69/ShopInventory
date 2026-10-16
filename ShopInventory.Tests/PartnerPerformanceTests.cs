using ClosedXML.Excel;
using ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers reading a window against the same dates in earlier years, partner by partner.
/// </summary>
/// <remarks>
/// The reports here are built by hand with totals that are deliberately not the sum of their
/// periods, so a test can tell which of the two the comparison read: the partner figures must be
/// the API's account totals, and the period rows the API's periods.
/// </remarks>
public sealed class PartnerPerformanceTests
{
    [Fact]
    public void The_earlier_windows_are_the_same_dates_shifted_back()
    {
        var (from, to) = PartnerPerformance.Shift(new DateTime(2026, 1, 1), new DateTime(2026, 10, 16), 2);

        Assert.Equal(new DateTime(2024, 1, 1), from);
        Assert.Equal(new DateTime(2024, 10, 16), to);
    }

    [Fact]
    public void A_window_ending_on_29_February_ends_on_the_28th_a_year_earlier()
    {
        var (_, to) = PartnerPerformance.Shift(new DateTime(2028, 1, 1), new DateTime(2028, 2, 29), 1);

        Assert.Equal(new DateTime(2027, 2, 28), to);
    }

    [Theory]
    [InlineData(2026, 1, 2026, 10, "2026")]
    [InlineData(2025, 7, 2026, 6, "2025–26")]
    public void A_window_is_named_by_its_year(int fromYear, int fromMonth, int toYear, int toMonth, string expected) =>
        Assert.Equal(
            expected,
            PartnerPerformance.YearLabel(new DateTime(fromYear, fromMonth, 1), new DateTime(toYear, toMonth, 1)));

    [Theory]
    [InlineData(150, 100, 50)]
    [InlineData(50, 100, -50)]
    [InlineData(50, -100, 150)]
    public void Change_is_measured_against_the_size_of_the_basis(decimal current, decimal basis, decimal expected) =>
        Assert.Equal(expected, PartnerPerformance.Change(current, basis));

    [Fact]
    public void A_partner_with_nothing_to_grow_from_has_no_percentage() =>
        Assert.Null(PartnerPerformance.Change(250m, 0m));

    [Fact]
    public void Each_partner_carries_its_own_account_totals_for_every_year()
    {
        var performance = PartnerPerformance.Build(
            [
                Window(2026, Account("FOR012", "Fortwell", usd: 9842.18m, volume: 6112m, invoices: 148)),
                Window(2025, Account("FOR012", "Fortwell", usd: 1262.89m, volume: 751m, invoices: 31)),
                Window(2024, Account("FOR012", "Fortwell", usd: 612.40m, volume: 352m, invoices: 14))
            ],
            readZig: false);

        var partner = Assert.Single(performance.Partners);
        Assert.Equal("FOR012", partner.Code);
        Assert.Equal([9842.18m, 1262.89m, 612.40m], partner.Years.Select(year => year.Revenue));
        Assert.Equal([6112m, 751m, 352m], partner.Years.Select(year => year.Volume));
        Assert.Equal(["2026", "2025", "2024"], performance.Windows.Select(window => window.Label));
    }

    [Fact]
    public void Partners_are_ordered_by_this_year_and_kept_when_they_only_traded_before()
    {
        var performance = PartnerPerformance.Build(
            [
                Window(2026,
                    Account("BP0001", "Small now", usd: 100m),
                    Account("BP0002", "Large now", usd: 900m),
                    Account("BP0003", "Lost", usd: 0m, invoices: 0)),
                Window(2025,
                    Account("BP0001", "Small now", usd: 400m),
                    Account("BP0003", "Lost", usd: 700m))
            ],
            readZig: false);

        Assert.Equal(["BP0002", "BP0001", "BP0003"], performance.Partners.Select(partner => partner.Code));

        var lost = performance.Partners.Single(partner => partner.Code == "BP0003");
        Assert.Equal([0m, 700m], lost.Years.Select(year => year.Revenue));

        // A partner that only appears in an earlier year still has a figure for this one.
        var large = performance.Partners.Single(partner => partner.Code == "BP0002");
        Assert.Equal([900m, 0m], large.Years.Select(year => year.Revenue));
    }

    [Fact]
    public void A_partner_that_traded_in_no_window_is_left_out()
    {
        var performance = PartnerPerformance.Build(
            [
                Window(2026, Account("BP0001", "Trades", usd: 10m), Account("BP0009", "Idle", usd: 0m, invoices: 0)),
                Window(2025, Account("BP0009", "Idle", usd: 0m, invoices: 0))
            ],
            readZig: false);

        Assert.Equal(["BP0001"], performance.Partners.Select(partner => partner.Code));
    }

    [Fact]
    public void The_totals_add_every_partner_per_year()
    {
        var performance = PartnerPerformance.Build(
            [
                Window(2026, Account("BP0001", "A", usd: 100m, volume: 10m), Account("BP0002", "B", usd: 50m, volume: 4m)),
                Window(2025, Account("BP0001", "A", usd: 80m, volume: 8m))
            ],
            readZig: false);

        Assert.Equal([150m, 80m], performance.Totals.Select(total => total.Revenue));
        Assert.Equal([14m, 8m], performance.Totals.Select(total => total.Volume));
    }

    [Fact]
    public void Revenue_reads_the_currency_asked_for_and_never_adds_the_other()
    {
        var performance = PartnerPerformance.Build(
            [Window(2026, Account("BP0001", "A", usd: 100m, zig: 2500m, creditedZig: 40m))],
            readZig: true);

        var year = Assert.Single(performance.Partners).Years[0];
        Assert.Equal(2500m, year.Revenue);
        Assert.Equal(40m, year.Credited);
    }

    [Fact]
    public void Revenue_per_litre_is_empty_where_nothing_converted()
    {
        Assert.Null(new PartnerPerformance.Figures(100m, 0m, 0m, 1, 0).RevenuePerLitre);
        Assert.Equal(2m, new PartnerPerformance.Figures(100m, 50m, 0m, 1, 0).RevenuePerLitre);
    }

    [Fact]
    public void Period_rows_line_up_by_position_and_follow_the_partner_asked_for()
    {
        var current = Window(2026, Account("BP0001", "A", usd: 1m), Account("BP0002", "B", usd: 1m));
        current.Periods =
        [
            Period(2026, 9, ("BP0001", 300m, 30m), ("BP0002", 5m, 1m)),
            Period(2026, 10, ("BP0001", -20m, -2m))
        ];

        var prior = Window(2025, Account("BP0001", "A", usd: 1m));
        prior.Periods =
        [
            Period(2025, 9, ("BP0001", 100m, 10m)),
            Period(2025, 10, ("BP0001", 40m, 4m))
        ];

        var performance = PartnerPerformance.Build([current, prior], readZig: false);

        var one = performance.PeriodsFor("BP0001");
        Assert.Equal([300m, 100m], one[0].Revenue);
        Assert.Equal([-20m, 40m], one[1].Revenue);
        Assert.Equal([-2m, 4m], one[1].Volume);

        var all = performance.PeriodsFor(null);
        Assert.Equal([305m, 100m], all[0].Revenue);
    }

    [Fact]
    public void A_period_the_window_cuts_short_says_where()
    {
        var current = Window(2026, Account("BP0001", "A", usd: 1m));
        current.Periods = [Period(2026, 9, ("BP0001", 1m, 1m)), Period(2026, 10, ("BP0001", 1m, 1m))];

        var rows = PartnerPerformance.Build([current], readZig: false).PeriodsFor(null);

        Assert.Equal("September", rows[0].Label);
        Assert.Equal("October (to 16 Oct)", rows[1].Label);
        Assert.Equal("Oct", rows[1].ShortLabel);
    }

    [Fact]
    public void The_workbook_carries_every_year_and_the_change_on_the_year_before()
    {
        var performance = PartnerPerformance.Build(
            [
                Window(2026, Account("FOR012", "Fortwell", usd: 300m, volume: 30m)),
                Window(2025, Account("FOR012", "Fortwell", usd: 200m, volume: 20m)),
                Window(2024, Account("FOR012", "Fortwell", usd: 100m, volume: 10m))
            ],
            readZig: false);

        using var workbook = new XLWorkbook(new MemoryStream(
            new ReportExportService().ExportPartnerPerformanceToExcel(performance, "Partner Performance", readZig: false)));

        var sheet = workbook.Worksheet("Partners");
        var header = sheet.RowsUsed().First(row => row.Cell(1).GetString() == "Card Code");
        var data = sheet.Row(header.RowNumber() + 1);

        Assert.Equal("Revenue USD 2026", header.Cell(3).GetString());
        Assert.Equal("Revenue vs 2025", header.Cell(6).GetString());
        Assert.Equal("FOR012", data.Cell(1).GetString());
        Assert.Equal(300m, data.Cell(3).GetValue<decimal>());
        Assert.Equal(100m, data.Cell(5).GetValue<decimal>());
        Assert.Equal(0.5m, data.Cell(6).GetValue<decimal>());
        Assert.Equal(30m, data.Cell(7).GetValue<decimal>());
    }

    private static GetItemVolumeSalesReportResult Window(int year, params ItemVolumeSalesAccountResult[] accounts) => new()
    {
        FromDateUtc = new DateTime(year, 1, 1),
        ToDateUtc = new DateTime(year, 10, 16),
        Grouping = ItemVolumeSalesGrouping.Monthly,
        AccountTotals = accounts.ToList()
    };

    private static ItemVolumeSalesAccountResult Account(
        string code,
        string name,
        decimal usd,
        decimal volume = 0m,
        int invoices = 1,
        decimal zig = 0m,
        decimal creditedZig = 0m) => new()
    {
        CardCode = code,
        CardName = name,
        NetRevenueUsd = usd,
        NetRevenueZig = zig,
        CreditedSalesZig = creditedZig,
        NetVolume = volume,
        InvoiceCount = invoices
    };

    private static ItemVolumeSalesPeriodResult Period(int year, int month, params (string Code, decimal Usd, decimal Volume)[] accounts)
    {
        var start = new DateTime(year, month, 1);

        return new ItemVolumeSalesPeriodResult
        {
            PeriodStartUtc = start,
            PeriodEndUtc = start.AddMonths(1).AddDays(-1),
            Accounts = accounts
                .Select(account => new ItemVolumeSalesAccountResult
                {
                    CardCode = account.Code,
                    NetRevenueUsd = account.Usd,
                    NetVolume = account.Volume
                })
                .ToList()
        };
    }
}
