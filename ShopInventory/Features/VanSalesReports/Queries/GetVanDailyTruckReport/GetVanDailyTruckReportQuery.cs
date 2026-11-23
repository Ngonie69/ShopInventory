using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanDailyTruckReport;

/// <summary>
/// The daily truck report: one row per route per day, and each route's period against its target.
/// </summary>
/// <remarks>
/// <para>
/// This is the hand-kept "VANSALES DAILY REPORT" workbook, read out of the system instead of typed
/// into a sheet. The office kept one row per truck per day (route, customers, departure, CCR, PCR,
/// sales, Ecocash, account sales, odometer, fuel) and a monthly summary against target. Reconciled
/// against production for the week of 1 October 2026, the sales agreed to the dollar once VAT was
/// counted and the rates did not: they were typed, and these are measured.
/// </para>
/// <para>
/// The grain is the truck, which is the route. Two reps share a truck, so calls and takings are
/// pooled across them exactly as <see cref="VanTruckDays"/> pools them for every other report.
/// </para>
/// </remarks>
/// <param name="FromDate">First trading day, CAT, inclusive.</param>
/// <param name="ToDate">Last trading day, CAT, inclusive.</param>
public sealed record GetVanDailyTruckReportQuery(DateTime FromDate, DateTime ToDate)
    : IRequest<ErrorOr<VanDailyTruckReportResult>>;

/// <summary>
/// The report. <c>Days</c> holds every working day of the period for every live route, reported or
/// not, plus any other day a truck traded on. <c>Trucks</c> is one line per route over the period and
/// <c>Total</c> the fleet.
/// </summary>
/// <param name="FromDate">First trading day of the period, CAT, inclusive.</param>
/// <param name="ToDate">Last trading day of the period, CAT, inclusive.</param>
/// <param name="GeneratedAtUtc">When the report was built, in UTC.</param>
/// <param name="WorkingDays">Monday-to-Friday days in the period: what a period's target is built on.</param>
/// <param name="WorkingDaysElapsed">Those of them that are not in the future.</param>
/// <param name="Trucks">One line per route over the period.</param>
/// <param name="Days">One row per route per day: every working day for every live route, plus any other day a truck traded.</param>
/// <param name="Total">The whole fleet over the period.</param>
/// <param name="Exceptions">How many truck-days in the period each check picked out.</param>
/// <param name="SalesOutsideRoutes">
/// Van takings in the period made by a login assigned to no route. In no row above, and stated so
/// the rows are not read as the whole.
/// </param>
public sealed record VanDailyTruckReportResult(
    DateTime FromDate,
    DateTime ToDate,
    DateTime GeneratedAtUtc,
    int WorkingDays,
    int WorkingDaysElapsed,
    IReadOnlyList<VanDailyTruckSummaryDto> Trucks,
    IReadOnlyList<VanDailyTruckDayDto> Days,
    VanDailyTruckSummaryDto Total,
    VanDailyTruckExceptionsDto Exceptions,
    decimal SalesOutsideRoutes)
{
    /// <summary>Whether SAP's booked gross profit could be read for the dollar sales, and what it means.</summary>
    public ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport.ManagementMarginStatus? Margin { get; init; }

    /// <summary><c>SalesOutsideRoutes</c> by how it was paid, split as a day's row splits its <c>Sales</c>.</summary>
    public VanDailyTruckTenders SalesOutsideRoutesByTender { get; init; } = VanDailyTruckTenders.None;
}

/// <summary>US dollars, VAT included, by tender: the same five columns a day's row carries.</summary>
/// <param name="Cash">Cash takings in US dollars, VAT included.</param>
/// <param name="Ecocash">Ecocash takings in US dollars, VAT included.</param>
/// <param name="Innbucks">InnBucks takings in US dollars, VAT included.</param>
/// <param name="OtherTender">Card, transfer, or any tender that is none of the three named.</param>
/// <param name="Untendered">Sales that name no tender at all.</param>
public sealed record VanDailyTruckTenders(decimal Cash, decimal Ecocash, decimal Innbucks, decimal OtherTender, decimal Untendered)
{
    public static readonly VanDailyTruckTenders None = new(0m, 0m, 0m, 0m, 0m);
}

/// <summary>One truck on one day.</summary>
/// <remarks>
/// Money is US dollars and includes VAT. Takings in any other currency are in
/// <c>OtherCurrencySales</c> and are in none of the dollar figures. A null is "not recorded", never
/// zero: no Start Day leaves the times and the odometer null, and a day with no check-ins has no PCR.
/// </remarks>
/// <param name="RouteId">The route the truck runs.</param>
/// <param name="RouteCode">The route's short code.</param>
/// <param name="RouteName">The route's display name.</param>
/// <param name="Territory">The territory the route belongs to; null when none is set.</param>
/// <param name="TradingDate">The trading day, CAT, with no time of day.</param>
/// <param name="IsWorkingDay">Monday to Friday; the days a target applies to.</param>
/// <param name="IsComplete">The day is over. Today is not, and is not judged against its target or for not reporting.</param>
/// <param name="HasDayRecord">A Start Day or End Day record exists for the truck on this day.</param>
/// <param name="HasActivity">The truck left any trace: a Start Day, a check-in or a sale.</param>
/// <param name="AreaName">Where the truck worked: the office's entry, else the day's own, else the plan.</param>
/// <param name="AreaSource"><c>Office</c>, <c>Day</c> or <c>Plan</c>, saying which of those it is; null with no area.</param>
/// <param name="TimeOut">Earliest departure either rep recorded, CAT.</param>
/// <param name="TimeIn">Latest return either rep recorded, CAT. See <c>ReturnedLate</c>.</param>
/// <param name="ReturnedLate">The day was closed on a later date than it was traded, so <c>TimeIn</c> is when End Day was tapped, not when the truck got back.</param>
/// <param name="PlannedCustomers">How many shops the day's calls are measured against. See <c>PlanBasis</c>.</param>
/// <param name="PlannedCallsMade">Check-ins that count towards the plan: those at shops on the round, or every check-in against the whole list.</param>
/// <param name="PlanBasis">
/// <c>Round</c> when the plan is the day's round: the shops the truck is scheduled for on the weekday
/// or has called on before in the same area. <c>List</c> when no round can be named and the plan is
/// the truck's whole customer list at Start Day, which reads low. Null with no plan at all.
/// </param>
/// <param name="CustomersVisited">Distinct shops either rep checked into, on the round or not.</param>
/// <param name="ProductiveCalls">Distinct shops that bought, plus one if any sale names no shop.</param>
/// <param name="CallsMade">Shops checked into or sold to.</param>
/// <param name="NewCustomers">Customers first recorded on this day by a rep on the truck.</param>
/// <param name="SalesCount">How many sales the truck made in US dollars.</param>
/// <param name="Sales">US dollar takings, VAT included; excludes other currencies and <c>AccountSales</c>.</param>
/// <param name="Cash">Cash takings in US dollars, VAT included.</param>
/// <param name="Ecocash">Ecocash takings in US dollars, VAT included.</param>
/// <param name="Innbucks">InnBucks takings in US dollars, VAT included.</param>
/// <param name="OtherTender">Card, transfer, or any tender that is none of the three named, in US dollars.</param>
/// <param name="Untendered">Sales that name no tender at all, in US dollars.</param>
/// <param name="OtherCurrencySales">Takings in any currency but US dollars, left out of every dollar figure; null when there are none.</param>
/// <param name="AccountSales">Sales on account, invoiced in SAP outside the van app and entered by the office. Not part of <c>Sales</c>.</param>
/// <param name="OtherCurrencyCodes">The currencies in <c>OtherCurrencySales</c>, upper-cased and joined with "/"; null when there are none.</param>
/// <param name="DeclaredTotal">What the reps declared handing in at End Day, summed over the route's days; null when no declaration was made.</param>
/// <param name="StartingMileage">Odometer on leaving, in kilometres.</param>
/// <param name="ClosingMileage">Odometer on returning, in kilometres; null until End Day is tapped.</param>
/// <param name="Kilometres">Distance in kilometres.</param>
/// <param name="FuelLitres">Fuel issued to the truck, in litres, as entered by the office; null when none was entered.</param>
/// <param name="DailySalesTarget">The route's target for the day; null on a day that is not a working day or a route with none.</param>
/// <param name="Notes">The office's free-text note for the day; null when none.</param>
public sealed record VanDailyTruckDayDto(
    int RouteId,
    string RouteCode,
    string RouteName,
    string? Territory,
    DateTime TradingDate,
    bool IsWorkingDay,
    bool IsComplete,
    bool HasDayRecord,
    bool HasActivity,
    string? AreaName,
    string? AreaSource,
    DateTime? TimeOut,
    DateTime? TimeIn,
    bool ReturnedLate,
    int? PlannedCustomers,
    int PlannedCallsMade,
    string? PlanBasis,
    int CustomersVisited,
    int ProductiveCalls,
    int CallsMade,
    int NewCustomers,
    int SalesCount,
    decimal Sales,
    decimal Cash,
    decimal Ecocash,
    decimal Innbucks,
    decimal OtherTender,
    decimal Untendered,
    decimal? OtherCurrencySales,
    string? OtherCurrencyCodes,
    decimal? AccountSales,
    decimal? DeclaredTotal,
    int? StartingMileage,
    int? ClosingMileage,
    int? Kilometres,
    decimal? FuelLitres,
    decimal? DailySalesTarget,
    string? Notes)
{
    /// <summary>Planned calls made over the plan. Null with no plan to measure against.</summary>
    public double? CallComplianceRate =>
        PlannedCustomers is > 0 ? (double)PlannedCallsMade / PlannedCustomers.Value : null;

    /// <summary>
    /// SAP's booked gross profit on the posted dollar sales; null when none is costed or none was read.
    /// Like <c>Sales</c>, dollars only.
    /// </summary>
    public decimal? GrossProfit { get; init; }

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on.</summary>
    public decimal? CostedNetAmount { get; init; }

    /// <summary>How many of the dollar sales <see cref="GrossProfit"/> was measured on.</summary>
    public int CostedSalesCount { get; init; }

    /// <summary><see cref="GrossProfit"/> as a percentage of <see cref="CostedNetAmount"/>.</summary>
    public decimal? MarginPercent => GrossProfit is { } profit && CostedNetAmount is { } costed && costed != 0
        ? decimal.Round(profit / costed * 100m, 1, MidpointRounding.AwayFromZero)
        : null;

    /// <summary>Shops that bought over calls made. Null on a day with no check-ins.</summary>
    public double? ProductiveCallRate =>
        CustomersVisited > 0 && CallsMade > 0 ? (double)ProductiveCalls / CallsMade : null;

    /// <summary>Takings over the calls that bought.</summary>
    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 ? decimal.Round(Sales / ProductiveCalls, 2) : null;

    /// <summary>A working day that has passed with a target on it and takings under it.</summary>
    public bool BelowTarget => IsComplete && DailySalesTarget is { } target && Sales < target;

    /// <summary>A working day on which the truck left no trace at all: no Start Day, call or sale.</summary>
    public bool NotReported => IsComplete && IsWorkingDay && !HasActivity;

    /// <summary>
    /// One odometer reading without the other once the day is over, or a closing reading below the
    /// opening one. A truck still out today has no closing reading yet, and that is not a fault.
    /// </summary>
    public bool MileageNeedsReview =>
        (IsComplete && StartingMileage.HasValue != ClosingMileage.HasValue)
        || (StartingMileage is { } start && ClosingMileage is { } close && close < start);

    /// <summary>The truck covered distance and sold nothing.</summary>
    public bool DistanceWithoutSales => Kilometres is > 0 && Sales == 0m;
}

/// <summary>One route over the period, or the fleet.</summary>
/// <param name="RouteId">The route's id; null on the fleet line.</param>
/// <param name="RouteCode">The route's short code.</param>
/// <param name="RouteName">The route's display name.</param>
/// <param name="Territory">The territory the route belongs to; null when none is set.</param>
/// <param name="DailySalesTarget">The route's target for one working day; null with none.</param>
/// <param name="PeriodTarget">Daily target times the period's working days. Null with no target.</param>
/// <param name="TargetToDate">Daily target times the working days so far.</param>
/// <param name="Sales">US dollar takings over the period, VAT included.</param>
/// <param name="Ecocash">Ecocash takings over the period, in US dollars.</param>
/// <param name="AccountSales">Sales on account over the period, entered by the office; not part of <c>Sales</c>.</param>
/// <param name="SalesCount">How many dollar sales the truck made over the period.</param>
/// <param name="DaysReported">Working days so far on which the truck left any trace.</param>
/// <param name="WorkingDaysElapsed">Working days so far in the period; on the fleet line, the working days among the rows.</param>
/// <param name="CustomersVisited">Distinct shops checked into, summed over the days.</param>
/// <param name="ProductiveCalls">Productive calls summed over the days.</param>
/// <param name="NewCustomers">Customers first recorded in the period by a rep on the truck.</param>
/// <param name="PlannedCustomers">
/// Each day's round, summed over the days that have one. Only where no day in the period has a round
/// is it the whole lists instead, and <c>PlanBasis</c> is then <c>List</c>.
/// </param>
/// <param name="CcrCustomersVisited">Planned calls made on those same days: the CCR's top half.</param>
/// <param name="PlanBasis"><c>Round</c> or <c>List</c>, saying which the period's CCR is over. Null with no plan.</param>
/// <param name="DaysOnWholeList">
/// Days that had a list and no round. Where the period has round days these are left out of its CCR,
/// and this is how many.
/// </param>
/// <param name="PcrProductiveCalls">Productive calls on the days that have check-ins: the PCR's top half.</param>
/// <param name="PcrCallsMade">Calls made on the days that have check-ins: the PCR's bottom half.</param>
/// <param name="Kilometres">Distance driven over the period, in kilometres.</param>
/// <param name="FuelLitres">Fuel issued over the period, in litres.</param>
public sealed record VanDailyTruckSummaryDto(
    int? RouteId,
    string RouteCode,
    string RouteName,
    string? Territory,
    decimal? DailySalesTarget,
    decimal? PeriodTarget,
    decimal? TargetToDate,
    decimal Sales,
    decimal Ecocash,
    decimal AccountSales,
    int SalesCount,
    int DaysReported,
    int WorkingDaysElapsed,
    int CustomersVisited,
    int ProductiveCalls,
    int NewCustomers,
    int PlannedCustomers,
    int CcrCustomersVisited,
    string? PlanBasis,
    int DaysOnWholeList,
    int PcrProductiveCalls,
    int PcrCallsMade,
    int Kilometres,
    decimal FuelLitres)
{
    /// <summary>What is left of the period's target. Negative once it is passed.</summary>
    public decimal? BalanceToTarget => PeriodTarget is { } target ? target - Sales : null;

    /// <summary>
    /// The SAP business partner the truck sells on: the van account most of the route's active reps are
    /// assigned to. Null on the fleet line and for a route none of whose reps has an account. Reports
    /// name a truck by this partner's SAP name, which is what the office calls it.
    /// </summary>
    public string? BusinessPartnerCode { get; init; }

    /// <summary>
    /// SAP's booked gross profit on the posted dollar sales; null when none is costed or none was read.
    /// Like <c>Sales</c>, dollars only.
    /// </summary>
    public decimal? GrossProfit { get; init; }

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on.</summary>
    public decimal? CostedNetAmount { get; init; }

    /// <summary>How many of the dollar sales <see cref="GrossProfit"/> was measured on.</summary>
    public int CostedSalesCount { get; init; }

    /// <summary><see cref="GrossProfit"/> as a percentage of <see cref="CostedNetAmount"/>.</summary>
    public decimal? MarginPercent => GrossProfit is { } profit && CostedNetAmount is { } costed && costed != 0
        ? decimal.Round(profit / costed * 100m, 1, MidpointRounding.AwayFromZero)
        : null;

    /// <summary>Takings as a share of the period's target.</summary>
    public double? TargetAchieved =>
        PeriodTarget is > 0 ? (double)(Sales / PeriodTarget.Value) : null;

    public double? CallComplianceRate =>
        PlannedCustomers > 0 ? (double)CcrCustomersVisited / PlannedCustomers : null;

    public double? ProductiveCallRate =>
        PcrCallsMade > 0 ? (double)PcrProductiveCalls / PcrCallsMade : null;

    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 ? decimal.Round(Sales / ProductiveCalls, 2) : null;

    /// <summary>Takings for each kilometre driven.</summary>
    public decimal? SalesPerKilometre =>
        Kilometres > 0 ? decimal.Round(Sales / Kilometres, 2) : null;

    /// <summary>Kilometres driven for each litre issued over the period.</summary>
    public decimal? KilometresPerLitre =>
        FuelLitres > 0 ? decimal.Round(Kilometres / FuelLitres, 2) : null;
}

/// <summary>How many truck-days in the period each check picked out.</summary>
public sealed record VanDailyTruckExceptionsDto(
    int BelowTarget,
    int NotReported,
    int MileageNeedsReview,
    int DistanceWithoutSales,
    int ReturnedLate);
