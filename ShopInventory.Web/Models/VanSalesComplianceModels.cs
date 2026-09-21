using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

/// <summary>
/// The departure compliance report, mirroring the API's <c>DepartureComplianceReportResult</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored, like every other API DTO in this project, which makes nullability the thing to get
/// right: a property declared non-nullable here against a value the API can send as null makes
/// System.Text.Json throw, and the page reports "no data" rather than an error. Every nullable below
/// is nullable in the API for a reason — a rate with no denominator, a day never closed, a route that
/// was never assigned.
/// </remarks>
public class DepartureComplianceReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<DepartureComplianceDay> Days { get; set; } = [];
    public DepartureComplianceSummary Summary { get; set; } = new();

    /// <summary>
    /// Whether the vehicle figures can be trusted. Never null from the API, but defaulted here so
    /// a response from an older API — or a stubbed one — does not null-reference the page.
    /// </summary>
    public DepartureComplianceTelematicsStatus Telematics { get; set; } = new();
}

/// <summary>Whether the vehicle figures on this report can be trusted, and why not when they cannot.</summary>
/// <remarks>
/// <c>Reason</c> is printed verbatim. The page does not compose its own explanation, because an
/// absent vehicle column has four causes and each needs a different thing done about it.
/// </remarks>
public class DepartureComplianceTelematicsStatus
{
    public bool Enabled { get; set; }
    public bool Configured { get; set; }
    public bool Ready { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public DateTime? CoveredFrom { get; set; }
    public DateTime? CoveredThrough { get; set; }
    public string? Reason { get; set; }
}

/// <summary>How a day's truck matched the telematics fleet.</summary>
/// <remarks>
/// Carries the same <see cref="JsonStringEnumConverter"/> as the API's copy. Without it the value
/// crosses the wire as an integer and the two enums drift apart the first time a member is
/// reordered — silently, because an integer always deserializes into something.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TelematicsMatch
{
    NoRegistration,
    NotInFleet,
    Matched
}

/// <summary>What the vehicle did on this day, beside what the rep recorded.</summary>
/// <remarks>
/// Null if and only if telematics is off for the whole report. Every other empty state — no
/// truck named, a truck the fleet does not hold, a truck that reported nothing — arrives as a
/// record with <see cref="Match"/> and <see cref="HasRollup"/> saying which.
/// </remarks>
public class DepartureComplianceTelematicsDay
{
    public string? Registration { get; set; }
    public TelematicsMatch Match { get; set; }
    public bool HasRollup { get; set; }
    public bool MovementRead { get; set; }
    public bool OdometerRead { get; set; }

    public DateTime? FirstIgnitionOn { get; set; }
    public DateTime? FirstDeparture { get; set; }
    public DateTime? LastIgnitionOff { get; set; }
    public double? DepartureLatitude { get; set; }
    public double? DepartureLongitude { get; set; }

    public int? IgnitionCycleCount { get; set; }
    public int? DrivingMinutes { get; set; }
    public int? IdleMinutes { get; set; }
    public int? DistanceKm { get; set; }
    public bool OdometerReset { get; set; }
    public bool TerminalChanged { get; set; }

    /// <summary>Why this vehicle may report nothing — workshop, tracker repair, retired.</summary>
    public string? VehicleStateLabel { get; set; }

    /// <summary>First movement where there is one, falling back to the first ignition.</summary>
    public DateTime? VerifiedDeparture => FirstDeparture ?? FirstIgnitionOn;

    public bool IsVerified => VerifiedDeparture is not null;

    /// <summary>The vehicle reported, and it never left the depot.</summary>
    public bool DidNotMove => HasRollup && MovementRead && FirstDeparture is null;
}

public class DepartureComplianceDay
{
    public int? VanRouteDayId { get; set; }
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public DateTime TradingDate { get; set; }

    public string? Territory { get; set; }
    public string? RouteCode { get; set; }
    public string? RouteName { get; set; }
    public string? TruckRegNo { get; set; }

    /// <summary>Already CAT when it arrives — the API converts. Do not shift it again.</summary>
    public DateTime? TimeOut { get; set; }

    public DateTime? TimeIn { get; set; }

    public int PlannedCustomerCount { get; set; }
    public int CustomersVisited { get; set; }
    public int ProductiveCalls { get; set; }

    public int? RtiOut { get; set; }
    public int? RtiReturned { get; set; }

    public decimal SystemCash { get; set; }
    public decimal SystemEcocash { get; set; }
    public decimal SystemInnbucks { get; set; }
    public decimal SystemOther { get; set; }
    public decimal SystemUntendered { get; set; }
    public decimal SystemTotalSales { get; set; }

    public decimal? DeclaredCash { get; set; }
    public decimal? DeclaredEcocash { get; set; }
    public decimal? DeclaredInnbucks { get; set; }

    public string? Currency { get; set; }
    public int NewCustomers { get; set; }

    public int? StartingMileage { get; set; }
    public int? ClosingMileage { get; set; }

    public bool HasDayRecord { get; set; }
    public bool IsClosed { get; set; }
    public string? Notes { get; set; }

    // The API computes these too, but computed properties are not serialised, so they are derived
    // again here from the same figures. Kept identical to the API's definitions on purpose.

    public double? CallComplianceRate =>
        PlannedCustomerCount > 0 ? (double)CustomersVisited / PlannedCustomerCount : null;

    public double? ProductiveCallRate =>
        CustomersVisited > 0 ? (double)ProductiveCalls / CustomersVisited : null;

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
    /// The takings the rep is in a position to declare — the three tenders the sheet has columns for,
    /// which is deliberately not the day's sales. A swipe and an untendered sale have no box on the
    /// handset, so neither can ever appear in <see cref="DeclaredTotal"/>.
    /// </summary>
    public decimal SystemDeclarableTakings => SystemCash + SystemEcocash + SystemInnbucks;

    /// <summary>What the rep counted, less the takings they could count.</summary>
    public decimal? DeclaredVariance =>
        DeclaredTotal is { } declared ? decimal.Round(declared - SystemDeclarableTakings, 2) : null;

    /// <summary>Declarable money the rep did not count back. The figure to chase.</summary>
    public decimal? DeclaredShortfall =>
        DeclaredVariance is { } variance && variance < 0 ? -variance : null;

    /// <summary>
    /// Money counted back that the day cannot account for, even allowing every untendered sale to
    /// have been cash the rep collected.
    /// </summary>
    public decimal? DeclaredOverage =>
        DeclaredVariance is { } variance && variance > SystemUntendered
            ? variance - SystemUntendered
            : null;

    public int? RtiOutstanding =>
        RtiOut is { } issued && RtiReturned is { } returned ? issued - returned : null;

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;

    /// <summary>
    /// What the vehicle did, or null when telematics is off for the whole report.
    /// </summary>
    public DepartureComplianceTelematicsDay? Telematics { get; set; }

    /// <summary>
    /// The departure the day is judged on: the vehicle's where there is one, the handset's where
    /// there is not. Mirrors the API's own computed member — a fact about the data, not policy.
    /// </summary>
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

    /// <summary>The vehicle's distance less the rep's, in kilometres.</summary>
    public int? OdometerDivergenceKm =>
        Telematics?.DistanceKm is { } vehicle && KilometresTravelled is { } captured
            ? vehicle - captured
            : null;
}

public class DepartureComplianceSummary
{
    public int DayCount { get; set; }
    public int PlannedCustomerCount { get; set; }
    public int CustomersVisited { get; set; }
    public int ProductiveCalls { get; set; }
    public decimal TotalSales { get; set; }
    public int NewCustomers { get; set; }
    public int? KilometresTravelled { get; set; }

    public double? CallComplianceRate =>
        PlannedCustomerCount > 0 ? (double)CustomersVisited / PlannedCustomerCount : null;

    public double? ProductiveCallRate =>
        CustomersVisited > 0 ? (double)ProductiveCalls / CustomersVisited : null;

    public decimal? AverageOrderValue =>
        ProductiveCalls > 0 ? decimal.Round(TotalSales / ProductiveCalls, 2) : null;
}

public class RouteDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Territory { get; set; }
    public string? TruckRegNo { get; set; }

    /// <summary>
    /// The limits the load on this round is held to, in Celsius. Both null — the ordinary case —
    /// means the round carries nothing chilled and is never judged on temperature, so both must
    /// stay nullable: a non-nullable pair would make every ambient route read as 0 °C to 0 °C and
    /// breach on every reading.
    /// </summary>
    public decimal? TemperatureMinC { get; set; }

    /// <inheritdoc cref="TemperatureMinC"/>
    public decimal? TemperatureMaxC { get; set; }

    /// <summary>Which of the tracker's four probes is the load box, or null for the first that reports.</summary>
    public byte? TemperatureProbeChannel { get; set; }

    public bool IsActive { get; set; }
    public int AssignedUserCount { get; set; }

    public string DisplayLabel => string.IsNullOrWhiteSpace(Territory)
        ? Name
        : $"{Name} — {Territory}";

    /// <summary>The range as a person writes it, or null when this round keeps no limits.</summary>
    public string? TemperatureRangeLabel =>
        TemperatureMinC is { } minimum && TemperatureMaxC is { } maximum
            ? $"{minimum:0.#} to {maximum:0.#} °C"
            : null;
}

/// <summary>
/// How a route's truck registration matches the telematics fleet.
/// </summary>
/// <remarks>
/// Mirrors the API's <c>TelematicsVehiclesResult</c>. <c>Reason</c> is a sentence the page prints
/// verbatim, because an empty list has four different causes — switched off, no credentials, not
/// synced yet, or an account that genuinely holds no vehicles — and each needs a different thing
/// done about it.
/// </remarks>
public class TelematicsVehiclesResponse
{
    public bool Enabled { get; set; }
    public bool Configured { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public string? Reason { get; set; }
    public List<TelematicsVehicleDto> Vehicles { get; set; } = [];
}

/// <summary>One vehicle the telematics provider knows about.</summary>
public class TelematicsVehicleDto
{
    public string Registration { get; set; } = string.Empty;
    public string RegistrationNormalized { get; set; } = string.Empty;

    /// <summary>The fleet's own name, "306_AFQ9644". A hint beside the plate, never matched on.</summary>
    public string? ClientVehicleName { get; set; }

    public string? Description { get; set; }
    public bool HasAnyFuelSensor { get; set; }

    /// <summary>Null means no sync has looked yet, which is not the same as no probe.</summary>
    public bool? HasTemperatureProbe { get; set; }

    public bool IsActiveInFleet { get; set; }

    /// <summary>Why this vehicle may report nothing — workshop, tracker repair, retired.</summary>
    public string? StateLabel { get; set; }

    /// <summary>
    /// The muted text beside the plate in the picker: what the vehicle is, and anything that
    /// would stop it reporting.
    /// </summary>
    /// <remarks>
    /// The yard name is dropped when it already contains the plate — this fleet names vehicles
    /// "306_AFQ9644", so carrying both made the hint long enough to squeeze the registration
    /// itself down to "A…" in the menu. The plate is the thing being chosen and must never be
    /// the part that gets elided.
    /// </remarks>
    public string? Hint
    {
        get
        {
            var yardName = ClientVehicleName is { } name
                           && !string.IsNullOrWhiteSpace(Registration)
                           && name.Contains(Registration, StringComparison.OrdinalIgnoreCase)
                ? null
                : ClientVehicleName;

            var parts = new[] { yardName, Description, StateLabel }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// One area a route is expected to work, and when.
/// </summary>
/// <remarks>
/// Mirrors the API's <c>RouteStopDto</c> by hand, like everything else in this file. Both nullable
/// properties must stay nullable: a town truck has no cycle week and an upcountry route has no
/// weekday, and a non-nullable property here would make System.Text.Json throw on the null and the
/// page would report an empty schedule rather than a failure.
/// </remarks>
public class RouteStopDto
{
    public int Id { get; set; }
    public int RouteId { get; set; }
    public string RouteCode { get; set; } = string.Empty;
    public string RouteName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>The weekday worked, or null on a route that keeps no weekday.</summary>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>The week of the route's cycle, 1-based, or null on a round that repeats weekly.</summary>
    public int? WeekNumber { get; set; }

    /// <summary>0 is the standard plan for its day; 1 and above are the published alternatives.</summary>
    public int AlternateSet { get; set; }

    public int Sequence { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// The heading this stop groups under on the page: "Monday", "Week 1", "Unscheduled".
    /// </summary>
    /// <remarks>
    /// Says nothing about the alternative set. The heading answers "when", and an alternative is the
    /// same when arrived at differently — the page marks it with a chip beside this, so spelling it
    /// out here as well would print the word twice on one line.
    /// </remarks>
    public string ScheduleHeading => (DayOfWeek, WeekNumber) switch
    {
        ({ } day, { } week) => $"{day}, week {week}",
        ({ } day, null) => day.ToString(),
        (null, { } week) => $"Week {week}",
        _ => "Unscheduled"
    };

    /// <summary>
    /// What the stops of one heading sort and group by. Ordered the way the schedule prints: the
    /// cycle week first, since a route uses either a week or a weekday and never both.
    /// </summary>
    public (int Week, int Day, int Set) GroupKey =>
        (WeekNumber ?? 0, DayOfWeek is null ? 0 : (int)DayOfWeek.Value, AlternateSet);
}
