using ErrorOr;
using MediatR;
using ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;

namespace ShopInventory.Features.VanSalesReports.Queries.GetFleetAudit;

/// <summary>
/// Every vehicle over a period, with a day-by-day account of each.
/// </summary>
/// <remarks>
/// The compliance report asks "how did this rep do", one row per rep-day. This asks the same
/// period from the other side: what did each <em>truck</em> do. They read the same projection and
/// deliberately do not share a shape — a fleet manager wants a vehicle that is barely moving or
/// barely reporting, and a rep-day grid buries both.
/// </remarks>
public sealed record GetFleetAuditQuery(
    DateTime FromDate,
    DateTime ToDate,
    string? Registration = null
) : IRequest<ErrorOr<FleetAuditResult>>;

/// <summary>The fleet audit for one period.</summary>
/// <remarks>
/// <c>Telematics</c> says whether the projection can speak for this period. Its <c>Reason</c> is
/// printed verbatim.
/// </remarks>
public sealed record FleetAuditResult(
    DateTime FromDate,
    DateTime ToDate,
    List<FleetAuditVehicleDto> Vehicles,
    DepartureComplianceTelematicsStatusDto Telematics);

/// <summary>
/// One vehicle's period: what it is, what it can measure, and what it did.
/// </summary>
/// <remarks>
/// <para>
/// <c>DaysWithData</c> and <c>DaysSilent</c> are the pair worth reading together. A truck with
/// twenty working days and three silent ones has a tracker question; a truck with twenty silent
/// ones is not being tracked at all, whatever the fleet list says it is fitted with.
/// </para>
/// <para>
/// Sensor capability is given even when nothing was reported, because "no fuel figures" and "no
/// fuel sensor" are different findings and only one of them is worth chasing.
/// </para>
/// <para>
/// The fuel totals sum the days the fuel read succeeded; <c>FuelAllTrustworthy</c> is false when
/// any of those days carried an uncalibrated or unsettled figure, so a period total built partly
/// from provisional readings says so. <c>DaysWithTemperature</c> counts the days the probe was
/// asked, <c>DaysWithReadings</c> the days it answered — the gap between them is days the fridge
/// was silent. <c>MinutesOutsideLimits</c> is null when no day in the period was judged.
/// </para>
/// </remarks>
public sealed record FleetAuditVehicleDto(
    string Registration,
    string RegistrationNormalized,
    string? VehicleName,
    string? Description,
    string? BusinessPartnerCode,
    string? BusinessPartnerName,
    bool IsActiveInFleet,
    string? StateLabel,
    bool HasAnyFuelSensor,
    bool? HasTemperatureProbe,
    DateTime? LastTemperatureSeenAt,
    DateTime? LastReportedAt,

    int DaysWithData,
    int DaysSilent,
    int DaysMoved,
    int TotalDistanceKm,
    int TotalDrivingMinutes,
    int TotalIdleMinutes,
    int TotalIgnitionCycles,
    DateTime? EarliestDeparture,
    DateTime? LatestDeparture,

    decimal TotalSales,
    int SaleCount,
    string? Currency,

    int DaysWithFuel,
    decimal? FuelUsedLitres,
    bool FuelAllTrustworthy,
    int FuelFillCount,
    decimal FuelFilledLitres,

    int DaysWithTemperature,
    int DaysWithReadings,
    int DaysBreached,
    int? MinutesOutsideLimits,
    decimal? TemperatureMinC,
    decimal? TemperatureMaxC,

    List<FleetAuditDayDto> Days)
{
    /// <summary>Kilometres per day it actually moved, which is the figure that compares two rounds.</summary>
    public int? AverageDistanceKm =>
        DaysMoved > 0 ? (int)Math.Round((double)TotalDistanceKm / DaysMoved) : null;

    /// <summary>
    /// The share of driving time the engine spent going nowhere. Idling burns fuel and hours and
    /// is invisible on a distance figure.
    /// </summary>
    public double? IdleShare =>
        TotalDrivingMinutes + TotalIdleMinutes > 0
            ? (double)TotalIdleMinutes / (TotalDrivingMinutes + TotalIdleMinutes)
            : null;

    /// <summary>
    /// Whether this vehicle reported on no day at all in the period — a tracker to look at
    /// rather than a van to ask about.
    /// </summary>
    public bool NeverReported => DaysWithData == 0 && DaysSilent > 0;

    /// <summary>
    /// Takings per kilometre driven — what the fuel and the tyres bought.
    /// </summary>
    /// <remarks>
    /// Null, not zero, when the vehicle is not linked to a van account or drove no distance.
    /// A van with no account linked has takings nobody can attribute to it, and rendering that
    /// as 0.00 per km would read as a van that drove all month and sold nothing.
    /// </remarks>
    public decimal? SalesPerKm =>
        BusinessPartnerCode is not null && TotalDistanceKm > 0
            ? decimal.Round(TotalSales / TotalDistanceKm, 2)
            : null;

    /// <summary>Kilometres per sale — how far this van drives between doors.</summary>
    public decimal? KmPerSale =>
        BusinessPartnerCode is not null && SaleCount > 0
            ? decimal.Round((decimal)TotalDistanceKm / SaleCount, 1)
            : null;
}

/// <summary>One trading day for one vehicle.</summary>
/// <remarks>
/// <c>MovementRead</c> and <c>OdometerRead</c> are carried through rather than collapsed into the
/// figures, because a null distance on a day we could not read is a different thing from a null
/// distance on a day the van sat still, and the page has to say which.
/// </remarks>
public sealed record FleetAuditDayDto(
    DateTime TradingDate,
    bool HasRollup,
    bool MovementRead,
    bool OdometerRead,
    DateTime? FirstIgnitionOn,
    DateTime? FirstDeparture,
    DateTime? LastIgnitionOff,
    int? IgnitionCycleCount,
    int? DrivingMinutes,
    int? IdleMinutes,
    int? DistanceKm,
    bool OdometerReset,
    bool TerminalChanged,
    string? LastError,

    string? RepName,
    string? RouteName,
    int? RepOdometerKm,

    decimal? Sales,
    int? SaleCount,

    FleetAuditFuelDto? Fuel,
    FleetAuditTemperatureDto? Temperature)
{
    /// <summary>The vehicle reported and never left the depot.</summary>
    public bool DidNotMove => HasRollup && MovementRead && FirstDeparture is null;

    /// <summary>
    /// The vehicle's distance less the rep's, where both exist. The fleet page shows it without
    /// a tolerance: a fleet manager reading one truck's month wants the raw pair.
    /// </summary>
    public int? OdometerDivergenceKm =>
        DistanceKm is { } vehicle && RepOdometerKm is { } rep ? vehicle - rep : null;
}

/// <summary>
/// One day's fuel, as far as the vehicle's sensors can say. Null on the day when the vehicle has
/// no fuel sensor, or the fuel read has not succeeded — the vehicle's <c>HasAnyFuelSensor</c>
/// says which of the two.
/// </summary>
/// <remarks>
/// <para>
/// The figures are carried with their caveats rather than filtered by them. On this fleet the one
/// tank sender is calibrated but its readings come back unsettled for hours, and hiding every
/// unsettled figure would hide all of them. The page mutes them and says why instead.
/// </para>
/// <para>
/// <c>UsedLitres</c> is the provider's own estimate over the day, which accounts for fills; the
/// difference of the two tank levels does not, and is not offered.
/// </para>
/// </remarks>
public sealed record FleetAuditFuelDto(
    decimal? LevelStartLitres,
    decimal? LevelEndLitres,
    decimal? UsedLitres,
    decimal? ConsumedLitres,
    bool? IsCalibrated,
    bool? ReadingsSettled,
    int FillCount,
    decimal FilledLitres)
{
    /// <summary>
    /// Whether the figures can be taken at face value. An uncalibrated sender produces no usable
    /// result, and an unsettled reading is still being revised by the provider.
    /// </summary>
    public bool Trustworthy => IsCalibrated == true && ReadingsSettled == true;
}

/// <summary>
/// One day's cold chain: what the fridge probe read, and the limits the round was judged against.
/// Null on the day when the temperature read has not succeeded.
/// </summary>
/// <remarks>
/// A day read successfully with <c>SampleCount</c> zero is a finding in its own right — the probe
/// said nothing — and is deliberately not collapsed into null. On a round with limits set that is
/// the cold-chain blind spot; on one without, it is a van with no probe.
/// </remarks>
public sealed record FleetAuditTemperatureDto(
    byte? Channel,
    int SampleCount,
    decimal? MinC,
    decimal? MaxC,
    decimal? AvgC,
    DateTime? FirstSampleAt,
    DateTime? LastSampleAt,
    decimal? LimitMinC,
    decimal? LimitMaxC,
    int? MinutesAboveMax,
    int? MinutesBelowMin)
{
    /// <summary>Whether this round carried limits at all. Without them nothing is judged.</summary>
    public bool Judged => LimitMinC is not null || LimitMaxC is not null;

    /// <summary>Minutes outside the limits, or null on a round that had none.</summary>
    public int? MinutesOutside => Judged ? (MinutesAboveMax ?? 0) + (MinutesBelowMin ?? 0) : null;

    /// <summary>Limits were set, and the probe spent time outside them.</summary>
    public bool Breached => MinutesOutside > 0;
}
