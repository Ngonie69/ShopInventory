using System.Text.Json.Serialization;
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
    DepartureComplianceSummary Summary,
    DepartureComplianceTelematicsStatusDto Telematics
);

/// <summary>
/// Whether the vehicle figures on this report can be trusted, and why not when they cannot.
/// </summary>
/// <remarks>
/// <c>Reason</c> is a sentence the page prints verbatim and is null when everything is in order.
/// An absent vehicle column has four different causes — telematics switched off, no credentials,
/// the history does not reach this far back, the sync is stale — and each needs a different thing
/// done about it, so the page is told which rather than left to invent an explanation.
/// </remarks>
public sealed record DepartureComplianceTelematicsStatusDto(
    bool Enabled,
    bool Configured,
    bool Ready,
    DateTime? LastSyncedAt,
    DateTime? CoveredFrom,
    DateTime? CoveredThrough,
    string? Reason);

/// <summary>How a day's truck matched the telematics fleet.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TelematicsMatch
{
    /// <summary>Neither the day nor the route names a truck, so there is nothing to look up.</summary>
    NoRegistration,

    /// <summary>A truck is named but the provider does not know it — a typo, or a vehicle sold.</summary>
    NotInFleet,

    /// <summary>Matched to a vehicle. Whether it reported anything is a separate question.</summary>
    Matched
}

/// <summary>
/// What the vehicle did on this day, beside what the rep recorded.
/// </summary>
/// <remarks>
/// <para>
/// Nested rather than twenty more properties on a row DTO that already has forty, and hand-copied
/// into the portal like everything else here.
/// </para>
/// <para>
/// <b>Null if and only if telematics is unavailable for the whole report.</b> Whenever the feature
/// is on, every row carries one of these — including rows whose truck is unknown, which arrive as
/// <see cref="TelematicsMatch.NoRegistration"/>. That makes "the feature is off" and "this van has
/// no data" structurally different rather than a convention someone has to remember.
/// </para>
/// </remarks>
public sealed record DepartureComplianceTelematicsDto(
    string? Registration,
    TelematicsMatch Match,
    bool HasRollup,
    bool MovementRead,
    bool OdometerRead,
    DateTime? FirstIgnitionOn,
    DateTime? FirstDeparture,
    DateTime? LastIgnitionOff,
    double? DepartureLatitude,
    double? DepartureLongitude,
    int? IgnitionCycleCount,
    int? DrivingMinutes,
    int? IdleMinutes,
    int? DistanceKm,
    bool OdometerReset,
    bool TerminalChanged,
    string? VehicleStateLabel)
{
    /// <summary>
    /// The vehicle's own answer to "when did this van leave", or null when it did not.
    /// </summary>
    /// <remarks>
    /// First movement where there is one, falling back to the first ignition. They are different
    /// questions and the gap between them is real: across five days on one truck it ran from
    /// eighteen minutes to five and a half hours, because a driver warming a diesel or pulling a
    /// fridge down to temperature turns the key long before the van goes anywhere.
    /// </remarks>
    public DateTime? VerifiedDeparture => FirstDeparture ?? FirstIgnitionOn;

    /// <summary>Whether the vehicle can speak for this day at all.</summary>
    public bool IsVerified => VerifiedDeparture is not null;

    /// <summary>The vehicle reported, and it never left the depot.</summary>
    public bool DidNotMove => HasRollup && MovementRead && FirstDeparture is null;
}

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
    string? Notes,

    DepartureComplianceTelematicsDto? Telematics = null
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

    /// <summary>
    /// The departure the day is actually judged on: the vehicle's where there is one, the
    /// handset's where there is not.
    /// </summary>
    /// <remarks>
    /// A fact about the data rather than a policy, which is why it lives here with the other
    /// computed members and not in the page's rulebook. Whether being late by it counts against
    /// the rep is the policy, and that stays where the other thresholds are.
    /// </remarks>
    public DateTime? EffectiveDeparture => Telematics?.VerifiedDeparture ?? TimeOut;

    /// <summary>Whether a vehicle, rather than a handset, answered for this departure.</summary>
    public bool DepartureIsVerified => Telematics?.VerifiedDeparture is not null;

    /// <summary>
    /// How far the vehicle and the handset disagree about when the van left, in minutes.
    /// Positive means the van left after the rep said it did.
    /// </summary>
    public int? DepartureDiscrepancyMinutes =>
        Telematics?.VerifiedDeparture is { } vehicle && TimeOut is { } handset
            ? (int)Math.Round((vehicle - handset).TotalMinutes)
            : null;

    /// <summary>
    /// The vehicle's distance less the rep's, in kilometres. Positive means the vehicle recorded
    /// more than the rep wrote down.
    /// </summary>
    /// <remarks>
    /// Expect a small positive number as a matter of course: the tracker accumulates metres while
    /// the rep subtracts two whole-kilometre readings. The tolerance that decides when a gap is
    /// worth showing is policy and lives with the other thresholds.
    /// </remarks>
    public int? OdometerDivergenceKm =>
        Telematics?.DistanceKm is { } vehicle && KilometresTravelled is { } captured
            ? vehicle - captured
            : null;
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
