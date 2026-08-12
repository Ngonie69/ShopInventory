using ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the compliance arithmetic — CCR, PCR, AOV and the cash variance.
///
/// The distinction these tests exist to protect is the one between zero and unavailable. A van whose
/// route had no customers assigned when the day opened has no call compliance rate at all, and 0%
/// would read to a supervisor as total failure rather than as missing data. Every rate here is
/// therefore nullable, and each null case below is a real one seen in the data: a day opened before
/// the route was populated, a day with a departure but no calls, a day of calls where nothing sold.
/// </summary>
public sealed class DepartureComplianceMetricsTests
{
    [Fact]
    public void The_sheets_own_figures_come_out_as_written()
    {
        // The worked example from the paper form: 126 customers, CCR 97%, PCR 92%.
        var day = Day(planned: 126, visited: 122, productive: 112, sales: 2403m);

        Assert.Equal(0.968, day.CallComplianceRate!.Value, 3);
        Assert.Equal(0.918, day.ProductiveCallRate!.Value, 3);
        Assert.Equal(21.46m, day.AverageOrderValue);
    }

    [Fact]
    public void Ccr_is_visits_over_the_plan()
    {
        var day = Day(planned: 50, visited: 40, productive: 0, sales: 0m);

        Assert.Equal(0.8, day.CallComplianceRate);
    }

    [Fact]
    public void Pcr_is_measured_against_the_calls_made_not_the_plan()
    {
        // Ten of twenty planned calls made, and eight of those ten bought. PCR is 80%, not 40% — the
        // ten missed calls are already counted against the CCR and must not be charged twice.
        var day = Day(planned: 20, visited: 10, productive: 8, sales: 100m);

        Assert.Equal(0.5, day.CallComplianceRate);
        Assert.Equal(0.8, day.ProductiveCallRate);
    }

    [Fact]
    public void Ccr_is_unavailable_when_the_route_had_no_customers()
    {
        // Not zero. Nothing was planned, so nothing can be missed.
        var day = Day(planned: 0, visited: 3, productive: 2, sales: 60m);

        Assert.Null(day.CallComplianceRate);
    }

    [Fact]
    public void Pcr_and_aov_are_unavailable_on_a_day_with_no_calls()
    {
        var day = Day(planned: 30, visited: 0, productive: 0, sales: 0m);

        Assert.Equal(0, day.CallComplianceRate);
        Assert.Null(day.ProductiveCallRate);
        Assert.Null(day.AverageOrderValue);
    }

    [Fact]
    public void Pcr_is_zero_when_calls_were_made_and_nothing_sold()
    {
        // Distinct from the case above: the rep did the round and sold nothing, which is a real
        // finding rather than a gap in the data.
        var day = Day(planned: 30, visited: 25, productive: 0, sales: 0m);

        Assert.Equal(0d, day.ProductiveCallRate);
        Assert.Null(day.AverageOrderValue);
    }

    [Fact]
    public void Aov_divides_by_the_calls_that_bought()
    {
        var day = Day(planned: 20, visited: 20, productive: 4, sales: 400m);

        Assert.Equal(100m, day.AverageOrderValue);
    }

    [Fact]
    public void Kilometres_need_both_readings_and_never_run_backwards()
    {
        Assert.Equal(81, Mileage(871842, 871923).KilometresTravelled);
        Assert.Null(Mileage(871842, null).KilometresTravelled);
        Assert.Null(Mileage(null, 871923).KilometresTravelled);

        // An odometer that appears to have gone backwards is a typo. Reporting a negative distance
        // would quietly subtract from a fleet total.
        Assert.Null(Mileage(871923, 871842).KilometresTravelled);
    }

    [Fact]
    public void Declared_total_is_null_when_nothing_was_counted()
    {
        // Distinct from a declared zero, which is a claim that nothing was taken.
        Assert.Null(Declared(null, null, null).DeclaredTotal);
        Assert.Equal(0m, Declared(0m, null, null).DeclaredTotal);
    }

    [Fact]
    public void Variance_is_what_the_rep_counted_less_what_the_system_recorded()
    {
        var over = Declared(2500m, null, null, systemSales: 2403m);
        Assert.Equal(97m, over.DeclaredVariance);

        var short_ = Declared(2300m, null, null, systemSales: 2403m);
        Assert.Equal(-103m, short_.DeclaredVariance);

        var level = Declared(2000m, 300m, 103m, systemSales: 2403m);
        Assert.Equal(0m, level.DeclaredVariance);
    }

    [Fact]
    public void Outstanding_crates_need_both_counts()
    {
        Assert.Equal(4, Rti(40, 36).RtiOutstanding);
        Assert.Null(Rti(40, null).RtiOutstanding);
        Assert.Null(Rti(null, 36).RtiOutstanding);
    }

    [Fact]
    public void The_period_summary_weights_by_volume_rather_than_averaging_the_days()
    {
        // A day of 4 planned calls and a day of 196 are not equal opinions about the same number.
        // Averaging the two rates gives 75%; the honest figure is 98%.
        var summary = new DepartureComplianceSummary(
            DayCount: 2,
            PlannedCustomerCount: 200,
            CustomersVisited: 198,
            ProductiveCalls: 99,
            TotalSales: 990m,
            NewCustomers: 0,
            KilometresTravelled: 160);

        Assert.Equal(0.99, summary.CallComplianceRate);
        Assert.Equal(0.5, summary.ProductiveCallRate);
        Assert.Equal(10m, summary.AverageOrderValue);
    }

    private static DepartureComplianceDayDto Day(
        int planned,
        int visited,
        int productive,
        decimal sales) =>
        Build(planned, visited, productive, sales);

    private static DepartureComplianceDayDto Mileage(int? starting, int? closing) =>
        Build(0, 0, 0, 0m, startingMileage: starting, closingMileage: closing);

    private static DepartureComplianceDayDto Rti(int? issued, int? returned) =>
        Build(0, 0, 0, 0m, rtiOut: issued, rtiReturned: returned);

    private static DepartureComplianceDayDto Declared(
        decimal? cash,
        decimal? ecocash,
        decimal? innbucks,
        decimal systemSales = 0m) =>
        Build(0, 0, 0, systemSales, declaredCash: cash, declaredEcocash: ecocash, declaredInnbucks: innbucks);

    private static DepartureComplianceDayDto Build(
        int planned,
        int visited,
        int productive,
        decimal sales,
        int? startingMileage = null,
        int? closingMileage = null,
        int? rtiOut = null,
        int? rtiReturned = null,
        decimal? declaredCash = null,
        decimal? declaredEcocash = null,
        decimal? declaredInnbucks = null) =>
        new(
            VanRouteDayId: 1,
            UserId: Guid.NewGuid(),
            Username: "tinashe",
            FullName: "Tinashe Madziva",
            TradingDate: new DateTime(2026, 8, 12),
            Territory: "UPC",
            RouteCode: "GUR",
            RouteName: "Guruve",
            TruckRegNo: "AHF0218",
            TimeOut: new DateTime(2026, 8, 12, 7, 0, 0),
            TimeIn: new DateTime(2026, 8, 12, 19, 50, 0),
            PlannedCustomerCount: planned,
            CustomersVisited: visited,
            ProductiveCalls: productive,
            RtiOut: rtiOut,
            RtiReturned: rtiReturned,
            SystemCash: sales,
            SystemEcocash: 0m,
            SystemInnbucks: 0m,
            SystemOther: 0m,
            SystemTotalSales: sales,
            DeclaredCash: declaredCash,
            DeclaredEcocash: declaredEcocash,
            DeclaredInnbucks: declaredInnbucks,
            Currency: "USD",
            NewCustomers: 0,
            StartingMileage: startingMileage,
            ClosingMileage: closingMileage,
            HasDayRecord: true,
            IsClosed: true,
            Notes: null);
}
