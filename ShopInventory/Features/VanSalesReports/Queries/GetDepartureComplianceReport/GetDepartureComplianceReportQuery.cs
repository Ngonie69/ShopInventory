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
/// <remarks>
/// <b>The money on this record is in one currency, named by <c>Currency</c>.</b> That is the whole
/// reason the row can be reconciled at all: the rep declares a till in one currency, so the system
/// figure it is compared against has to be in the same one, or the variance is arithmetic between
/// two different kinds of money.
///
/// The currency chosen is the one the rep declared in, and only where nothing was declared does it
/// fall back to whichever currency took the most. Anything a van took in another currency that day
/// is in <c>OtherCurrencyTotals</c> — reported rather than dropped, and never folded into the
/// figures above.
///
/// This used to sum every currency into <c>SystemTotalSales</c> and stamp it with whichever one the
/// first sale happened to carry. A rep taking USD 100 and ZWG 3,000 and declaring the USD correctly
/// showed a USD 3,000 shortfall on a cash-control report.
/// </remarks>
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
    decimal SystemTotalSales,

    decimal? DeclaredCash,
    decimal? DeclaredEcocash,
    decimal? DeclaredInnbucks,

    string? Currency,
    List<VanSalesMoneyResult> OtherCurrencyTotals,
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

    /// <summary>
    /// Average order value: takings over the calls that bought, not over every call.
    ///
    /// In <c>Currency</c> only. A day that also took another currency has that in
    /// <c>OtherCurrencyTotals</c>, and mixing it in here would divide two kinds of money by one
    /// count.
    /// </summary>
    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 ? decimal.Round(SystemTotalSales / ProductiveCalls, 2) : null;

    /// <summary>
    /// True when the van also took money in a currency the figures above do not cover. The page has
    /// to say so, or a reader reconciling the till sees a total that is genuinely smaller than the
    /// day's takings and has no way to tell why.
    /// </summary>
    public bool HasOtherCurrencies => OtherCurrencyTotals.Count > 0;

    public int? KilometresTravelled =>
        StartingMileage is { } start && ClosingMileage is { } close && close >= start
            ? close - start
            : null;

    public decimal? DeclaredTotal =>
        DeclaredCash is null && DeclaredEcocash is null && DeclaredInnbucks is null
            ? null
            : (DeclaredCash ?? 0) + (DeclaredEcocash ?? 0) + (DeclaredInnbucks ?? 0);

    /// <summary>
    /// Declared minus recorded. Positive means the rep counted more than the system sold, which is
    /// usually an unrecorded sale; negative is the one to chase.
    /// </summary>
    public decimal? DeclaredVariance =>
        DeclaredTotal is { } declared ? decimal.Round(declared - SystemTotalSales, 2) : null;

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
    List<VanSalesMoneyResult> TotalsByCurrency,
    int NewCustomers,
    int? KilometresTravelled
)
{
    public double? CallComplianceRate =>
        PlannedCustomerCount > 0 ? (double)CustomersVisited / PlannedCustomerCount : null;

    public double? ProductiveCallRate =>
        CustomersVisited > 0 ? (double)ProductiveCalls / CustomersVisited : null;

    /// <summary>
    /// The currency that took the most across the period, or null when nothing sold. Every scalar
    /// money figure a caller derives from this summary has to name it.
    /// </summary>
    public VanSalesMoneyResult? PrimaryCurrency => TotalsByCurrency.FirstOrDefault();

    /// <summary>
    /// Average order value in the primary currency alone. A period spanning two currencies reports
    /// the other in <c>TotalsByCurrency</c>; it is never added in here.
    /// </summary>
    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 && PrimaryCurrency is { } primary
            ? decimal.Round(primary.Gross / ProductiveCalls, 2)
            : null;
}
