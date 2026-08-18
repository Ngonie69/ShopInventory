using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;

/// <summary>
/// The departure compliance report over a period.
/// </summary>
/// <remarks>
/// <c>FromDate</c> and <c>ToDate</c> are inclusive CAT trading days, not instants — a van's day
/// belongs to the van, not to the server's zone. <c>RouteCode</c> excludes days with no departure
/// record, because nothing on a loose visit says which route it belonged to.
/// </remarks>
public sealed record GetDepartureComplianceReportQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId = null,
    string? RouteCode = null
) : IRequest<ErrorOr<DepartureComplianceReportResult>>;

public sealed record DepartureComplianceReportResult(
    DateTime FromDate,
    DateTime ToDate,
    List<DepartureComplianceDayDto> Days,
    DepartureComplianceSummary Summary
);

/// <summary>
/// One rep's trading day, laid out as the departure compliance sheet reads.
///
/// Times are CAT, because that is the clock the sheet is written in and the one the rep worked to.
/// Rates are fractions (0.97), not percentages, so the presentation layer decides how to round.
/// </summary>
public sealed record DepartureComplianceDayDto(
    int? VanRouteDayId,
    Guid UserId,
    string Username,
    string? FullName,
    DateTime TradingDate,

    string? Territory,
    string? RouteCode,
    string? RouteName,
    string? TruckRegNo,

    DateTime? TimeOut,
    DateTime? TimeIn,

    int PlannedCustomerCount,
    int CustomersVisited,
    int ProductiveCalls,

    int? RtiOut,
    int? RtiReturned,

    decimal SystemCash,
    decimal SystemEcocash,
    decimal SystemInnbucks,
    decimal SystemOther,
    decimal SystemUntendered,
    decimal SystemTotalSales,

    decimal? DeclaredCash,
    decimal? DeclaredEcocash,
    decimal? DeclaredInnbucks,

    string? Currency,
    int NewCustomers,

    int? StartingMileage,
    int? ClosingMileage,

    bool HasDayRecord,
    bool IsClosed,
    string? Notes
)
{
    /// <summary>
    /// Call compliance: the customers called on, over the customers planned.
    ///
    /// Null rather than zero when the day has no denominator — a day opened before the route had
    /// customers, or one reconstructed from visits with no departure record at all. A CCR of 0% and
    /// "we cannot say" are different findings and a supervisor must not have to guess which they are
    /// looking at.
    /// </summary>
    public double? CallComplianceRate =>
        PlannedCustomerCount > 0 ? (double)CustomersVisited / PlannedCustomerCount : null;

    /// <summary>
    /// Productive calls over calls made — how many of the visits the rep actually made produced a
    /// sale. Measured against visits rather than against the plan on purpose: this is the rep's
    /// conversion on the doors they got to, and the doors they missed are already counted by the CCR.
    /// </summary>
    public double? ProductiveCallRate =>
        CustomersVisited > 0 ? (double)ProductiveCalls / CustomersVisited : null;

    /// <summary>Average order value: takings over the calls that bought, not over every call.</summary>
    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 ? decimal.Round(SystemTotalSales / ProductiveCalls, 2) : null;

    public int? KilometresTravelled =>
        StartingMileage is { } start && ClosingMileage is { } close && close >= start
            ? close - start
            : null;

    public decimal? DeclaredTotal =>
        DeclaredCash is null && DeclaredEcocash is null && DeclaredInnbucks is null
            ? null
            : (DeclaredCash ?? 0) + (DeclaredEcocash ?? 0) + (DeclaredInnbucks ?? 0);

    /// <summary>
    /// The takings the rep is in a position to declare — the three tenders the sheet has columns for.
    /// </summary>
    /// <remarks>
    /// Not the day's sales. A card swipe settles at the terminal and an untendered sale names no
    /// tender at all, and the handset offers no box for either, so neither can appear in
    /// <see cref="DeclaredTotal"/> however honest the rep is. Measuring a three-term declaration
    /// against a five-term total reported every such rep short by exactly the money they had no way
    /// to declare, which is what this exists to stop.
    /// </remarks>
    public decimal SystemDeclarableTakings => SystemCash + SystemEcocash + SystemInnbucks;

    /// <summary>
    /// What the rep counted, less the takings they could count. Like against like.
    /// </summary>
    /// <remarks>
    /// Read this with <see cref="SystemUntendered"/> rather than on its own: a positive variance up to
    /// that figure is a rep who counted an untendered sale as cash, not an overage.
    /// <see cref="DeclaredShortfall"/> and <see cref="DeclaredOverage"/> are the two findings, and
    /// they already allow for it.
    /// </remarks>
    public decimal? DeclaredVariance =>
        DeclaredTotal is { } declared ? decimal.Round(declared - SystemDeclarableTakings, 2) : null;

    /// <summary>
    /// Money the sheet says was taken in a declarable tender and the rep did not count back. Null
    /// where there is none — this is the figure to chase, so it is present only when there is one.
    /// </summary>
    /// <remarks>
    /// The untendered bucket cannot excuse a shortfall the way it can excuse an overage: a sale whose
    /// tender went unrecorded can only ever add to what the rep had in hand, never subtract from it.
    /// </remarks>
    public decimal? DeclaredShortfall =>
        DeclaredVariance is { } variance && variance < 0 ? -variance : null;

    /// <summary>
    /// Money counted back that the day cannot account for even after allowing every untendered sale
    /// to have been cash the rep collected. Usually a sale that was made and never recorded.
    /// </summary>
    public decimal? DeclaredOverage =>
        DeclaredVariance is { } variance && variance > SystemUntendered
            ? variance - SystemUntendered
            : null;

    public int? RtiOutstanding =>
        RtiOut is { } issued && RtiReturned is { } returned ? issued - returned : null;
}

/// <summary>
/// The period as one line. Rates are recomputed from the totals rather than averaged across days,
/// because a day with four planned calls and a day with two hundred are not equal opinions about the
/// same number.
/// </summary>
public sealed record DepartureComplianceSummary(
    int DayCount,
    int PlannedCustomerCount,
    int CustomersVisited,
    int ProductiveCalls,
    decimal TotalSales,
    int NewCustomers,
    int? KilometresTravelled
)
{
    public double? CallComplianceRate =>
        PlannedCustomerCount > 0 ? (double)CustomersVisited / PlannedCustomerCount : null;

    public double? ProductiveCallRate =>
        CustomersVisited > 0 ? (double)ProductiveCalls / CustomersVisited : null;

    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 ? decimal.Round(TotalSales / ProductiveCalls, 2) : null;
}
