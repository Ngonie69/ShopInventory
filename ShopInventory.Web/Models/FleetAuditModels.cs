using System.Globalization;

namespace ShopInventory.Web.Models;

/// <summary>
/// The fleet over a period, mirroring the API's <c>FleetAuditResult</c> by hand like every other
/// DTO in this project.
/// </summary>
/// <remarks>
/// Nullability is the thing to get right, as always here: a property declared non-nullable
/// against a value the API can send as null makes System.Text.Json throw, and the page then
/// reports "no data" rather than an error. Every nullable below is nullable for a reason —
/// a vehicle with no account linked, a day the tracker never reported, a distance nobody read.
/// </remarks>
public class FleetAuditResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<FleetAuditVehicle> Vehicles { get; set; } = [];

    /// <summary>Whether the projection can speak for this period. <c>Reason</c> is printed verbatim.</summary>
    public DepartureComplianceTelematicsStatus Telematics { get; set; } = new();
}

/// <summary>One vehicle's period.</summary>
public class FleetAuditVehicle
{
    public string Registration { get; set; } = string.Empty;
    public string RegistrationNormalized { get; set; } = string.Empty;

    /// <summary>The fleet's own name for it — "306_AFQ9644". Not a registration.</summary>
    public string? VehicleName { get; set; }

    public string? Description { get; set; }

    /// <summary>The van sales account this truck carries, or null when nothing is linked.</summary>
    public string? BusinessPartnerCode { get; set; }

    public string? BusinessPartnerName { get; set; }

    public bool IsActiveInFleet { get; set; }

    /// <summary>Why it may report nothing — workshop, tracker repair, retired.</summary>
    public string? StateLabel { get; set; }

    public bool HasAnyFuelSensor { get; set; }

    /// <summary>Null means no sync has looked yet, which is not the same as no probe.</summary>
    public bool? HasTemperatureProbe { get; set; }

    public DateTime? LastTemperatureSeenAt { get; set; }
    public DateTime? LastReportedAt { get; set; }

    public int DaysWithData { get; set; }
    public int DaysSilent { get; set; }
    public int DaysMoved { get; set; }
    public int TotalDistanceKm { get; set; }
    public int TotalDrivingMinutes { get; set; }
    public int TotalIdleMinutes { get; set; }
    public int TotalIgnitionCycles { get; set; }
    public DateTime? EarliestDeparture { get; set; }
    public DateTime? LatestDeparture { get; set; }

    public decimal TotalSales { get; set; }
    public int SaleCount { get; set; }
    public string? Currency { get; set; }

    public int DaysWithFuel { get; set; }

    /// <summary>Null when no day gave a figure — nobody measured it, not a van that burned nothing.</summary>
    public decimal? FuelUsedLitres { get; set; }

    /// <summary>False when any day behind the total was uncalibrated or still being revised.</summary>
    public bool FuelAllTrustworthy { get; set; }

    public int FuelFillCount { get; set; }
    public decimal FuelFilledLitres { get; set; }

    /// <summary>Days the probe was asked.</summary>
    public int DaysWithTemperature { get; set; }

    /// <summary>Days it answered. The gap between the two is days the fridge said nothing.</summary>
    public int DaysWithReadings { get; set; }

    public int DaysBreached { get; set; }

    /// <summary>Null when no day in the period carried limits to judge against.</summary>
    public int? MinutesOutsideLimits { get; set; }

    public decimal? TemperatureMinC { get; set; }
    public decimal? TemperatureMaxC { get; set; }

    public List<FleetAuditDay> Days { get; set; } = [];

    /// <summary>Kilometres per day it actually moved — the figure that compares two rounds.</summary>
    public int? AverageDistanceKm =>
        DaysMoved > 0 ? (int)Math.Round((double)TotalDistanceKm / DaysMoved) : null;

    /// <summary>The share of engine time spent going nowhere. Invisible on a distance figure.</summary>
    public double? IdleShare =>
        TotalDrivingMinutes + TotalIdleMinutes > 0
            ? (double)TotalIdleMinutes / (TotalDrivingMinutes + TotalIdleMinutes)
            : null;

    /// <summary>Reported on no day at all — a tracker to look at, not a van to ask about.</summary>
    public bool NeverReported => DaysWithData == 0 && DaysSilent > 0;

    /// <summary>
    /// Takings per kilometre. Null, not zero, when no account is linked — a van nobody can
    /// attribute takings to has not sold nothing, it has simply not been mapped.
    /// </summary>
    public decimal? SalesPerKm =>
        BusinessPartnerCode is not null && TotalDistanceKm > 0
            ? decimal.Round(TotalSales / TotalDistanceKm, 2)
            : null;

    /// <summary>Kilometres per sale — how far this van drives between doors.</summary>
    public decimal? KmPerSale =>
        BusinessPartnerCode is not null && SaleCount > 0
            ? decimal.Round((decimal)TotalDistanceKm / SaleCount, 1)
            : null;

    /// <summary>The van account as a person reads it, or why there is none.</summary>
    public string AccountLabel =>
        BusinessPartnerCode is null
            ? "Not linked"
            : string.IsNullOrWhiteSpace(BusinessPartnerName)
                ? BusinessPartnerCode
                : $"{BusinessPartnerCode} — {BusinessPartnerName}";
}

/// <summary>One trading day for one vehicle.</summary>
public class FleetAuditDay
{
    public DateTime TradingDate { get; set; }

    public bool HasRollup { get; set; }

    /// <summary>
    /// Whether the movement read succeeded. False with <see cref="HasRollup"/> true means we
    /// tried and could not, which is a different thing from a van that sat still.
    /// </summary>
    public bool MovementRead { get; set; }

    public bool OdometerRead { get; set; }

    public DateTime? FirstIgnitionOn { get; set; }
    public DateTime? FirstDeparture { get; set; }
    public DateTime? LastIgnitionOff { get; set; }
    public int? IgnitionCycleCount { get; set; }
    public int? DrivingMinutes { get; set; }
    public int? IdleMinutes { get; set; }
    public int? DistanceKm { get; set; }
    public bool OdometerReset { get; set; }
    public bool TerminalChanged { get; set; }
    public string? LastError { get; set; }

    public string? RepName { get; set; }
    public string? RouteName { get; set; }
    public int? RepOdometerKm { get; set; }

    public decimal? Sales { get; set; }
    public int? SaleCount { get; set; }

    /// <summary>Null when the vehicle has no fuel sensor or the fuel read has not succeeded.</summary>
    public FleetAuditFuel? Fuel { get; set; }

    /// <summary>Null when the temperature read has not succeeded. Zero samples is a finding, not null.</summary>
    public FleetAuditTemperature? Temperature { get; set; }

    /// <summary>The vehicle reported and never left the depot.</summary>
    public bool DidNotMove => HasRollup && MovementRead && FirstDeparture is null;

    public int? OdometerDivergenceKm =>
        DistanceKm is { } vehicle && RepOdometerKm is { } rep ? vehicle - rep : null;

    /// <summary>
    /// What this day was, in one word, for the state column.
    /// </summary>
    /// <remarks>
    /// Four states rather than a blank: a day nobody read, a day the van sat still and a day it
    /// worked are three different findings, and only the middle one is about the driver.
    /// </remarks>
    public FleetDayState State =>
        !HasRollup ? FleetDayState.NoData
        : !MovementRead ? FleetDayState.NotRead
        : FirstDeparture is null ? FleetDayState.DidNotMove
        : FleetDayState.Worked;
}

/// <summary>One day's fuel, with the caveats the provider attaches to it.</summary>
public class FleetAuditFuel
{
    public decimal? LevelStartLitres { get; set; }
    public decimal? LevelEndLitres { get; set; }

    /// <summary>The provider's estimate over the day, which accounts for fills.</summary>
    public decimal? UsedLitres { get; set; }

    /// <summary>What the engine itself reported burning. Needs a CAN read; none of this fleet has one.</summary>
    public decimal? ConsumedLitres { get; set; }

    public bool? IsCalibrated { get; set; }

    /// <summary>False while the provider is still revising the readings — recent ones always are.</summary>
    public bool? ReadingsSettled { get; set; }

    public int FillCount { get; set; }
    public decimal FilledLitres { get; set; }

    public bool Trustworthy => IsCalibrated == true && ReadingsSettled == true;

    /// <summary>Why a figure is muted, or null when it can be read at face value.</summary>
    public string? Caveat =>
        IsCalibrated == false ? "sensor not calibrated"
        : IsCalibrated is null ? "calibration unknown"
        : ReadingsSettled != true ? "provisional"
        : null;
}

/// <summary>One day's cold chain, judged against the limits the round carried at the time.</summary>
public class FleetAuditTemperature
{
    public byte? Channel { get; set; }
    public int SampleCount { get; set; }
    public decimal? MinC { get; set; }
    public decimal? MaxC { get; set; }
    public decimal? AvgC { get; set; }
    public DateTime? FirstSampleAt { get; set; }
    public DateTime? LastSampleAt { get; set; }
    public decimal? LimitMinC { get; set; }
    public decimal? LimitMaxC { get; set; }
    public int? MinutesAboveMax { get; set; }
    public int? MinutesBelowMin { get; set; }

    public bool Judged => LimitMinC is not null || LimitMaxC is not null;

    public int? MinutesOutside => Judged ? (MinutesAboveMax ?? 0) + (MinutesBelowMin ?? 0) : null;

    public bool Breached => MinutesOutside > 0;

    /// <summary>The limits as a person reads them: "−18 to −12 °C", "at most 5 °C".</summary>
    public string? LimitsLabel =>
        (LimitMinC, LimitMaxC) switch
        {
            ({ } min, { } max) => $"{min:0.#} to {max:0.#} °C",
            (null, { } max) => $"at most {max:0.#} °C",
            ({ } min, null) => $"at least {min:0.#} °C",
            _ => null
        };
}

/// <summary>What one vehicle-day amounted to.</summary>
public enum FleetDayState
{
    /// <summary>Nothing was built for this day at all.</summary>
    NoData,

    /// <summary>A build was attempted and the provider could not be read.</summary>
    NotRead,

    /// <summary>The vehicle reported and never left the depot.</summary>
    DidNotMove,

    /// <summary>It went out.</summary>
    Worked
}

/// <summary>One van sales account, for the link picker.</summary>
public class VanSalesAccountOption
{
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }

    /// <summary>The registration already using this account, when one is.</summary>
    public string? LinkedTo { get; set; }
}

/// <summary>What one fleet-page cell shows: the figure, a line beneath it, and how it is inked.</summary>
/// <param name="Figure">The main value, or an em dash when there is none.</param>
/// <param name="Sub">The line beneath, which is where the caveat or the reason goes.</param>
/// <param name="Flagged">A finding: drawn in the page's one warning ink.</param>
/// <param name="Muted">A figure that exists but should not be read at face value.</param>
public readonly record struct FleetCell(string Figure, string? Sub, bool Flagged, bool Muted);

/// <summary>
/// The fuel and cold-chain cells of the fleet page, kept out of the Razor file so the wording can
/// be pinned by tests.
/// </summary>
/// <remarks>
/// <para>
/// The wording is the feature. "No fuel sensor" is a fitting and "fuel not reported" is a fault;
/// "no limits set" is a route nobody configured and "in range" is a load that stayed cold. Each
/// pair looks alike as an em dash and means something different to whoever reads it, so every
/// empty cell here says which empty it is.
/// </para>
/// <para>
/// Only a breach is flagged. A probe that said nothing on a round with limits is flagged too, on a
/// day the van actually ran, because that is the cold-chain blind spot: nobody can say the load
/// stayed cold. A van still in the yard has no load on the road, so its silence is not flagged. Fuel is never
/// flagged — on this fleet it is too thin to accuse anyone with — and is muted instead wherever
/// the provider says the figure is not settled.
/// </para>
/// </remarks>
public static class FleetAuditCells
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static FleetCell VehicleFuel(FleetAuditVehicle vehicle)
    {
        if (!vehicle.HasAnyFuelSensor)
        {
            return new FleetCell("—", "no fuel sensor", false, false);
        }

        if (vehicle.DaysWithFuel == 0)
        {
            return new FleetCell("—", "fuel not reported", false, false);
        }

        var figure = vehicle.FuelUsedLitres is { } used ? Litres(used) : "—";

        if (!vehicle.FuelAllTrustworthy)
        {
            var uncalibrated = vehicle.Days.Any(day => day.Fuel?.IsCalibrated == false);

            return new FleetCell(figure, uncalibrated ? "sensor not calibrated" : "provisional", false, true);
        }

        return new FleetCell(figure, Fills(vehicle.FuelFillCount, vehicle.FuelFilledLitres), false, false);
    }

    public static FleetCell VehicleColdChain(FleetAuditVehicle vehicle)
    {
        if (vehicle.DaysWithTemperature == 0)
        {
            return new FleetCell("—", "not read", false, false);
        }

        if (vehicle.DaysWithReadings == 0)
        {
            // Flagged only where somebody asked for the load to be kept cold and the van actually
            // ran. A van with no probe and no limits is simply not a fridge truck, and one that
            // never left had no load on the road to keep cold.
            var blind = vehicle.MinutesOutsideLimits is not null && vehicle.DaysMoved > 0;

            return new FleetCell("—", blind ? "no probe readings" : "no probe", blind, false);
        }

        var range = Range(vehicle.TemperatureMinC, vehicle.TemperatureMaxC);

        return vehicle.MinutesOutsideLimits switch
        {
            null => new FleetCell(range, "no limits set", false, false),
            0 => new FleetCell("in range", range, false, false),
            { } minutes => new FleetCell(
                Duration(minutes) + " out",
                vehicle.DaysBreached + " day" + (vehicle.DaysBreached == 1 ? "" : "s"),
                true,
                false)
        };
    }

    public static FleetCell DayFuel(FleetAuditVehicle vehicle, FleetAuditDay day)
    {
        if (day.Fuel is not { } fuel)
        {
            // The vehicle-level cell already says "no fuel sensor"; thirty rows repeating it
            // would bury the days that have something to say.
            return new FleetCell("—", vehicle.HasAnyFuelSensor && day.HasRollup ? "not read" : null, false, false);
        }

        var figure = fuel.UsedLitres is { } used
            ? Litres(used)
            : fuel.ConsumedLitres is { } burned ? Litres(burned) : "—";

        var sub = fuel.Caveat ?? Fills(fuel.FillCount, fuel.FilledLitres);

        return new FleetCell(figure, sub, false, fuel.Caveat is not null);
    }

    public static FleetCell DayColdChain(FleetAuditDay day)
    {
        if (day.Temperature is not { } cold)
        {
            return new FleetCell("—", null, false, false);
        }

        if (cold.SampleCount == 0)
        {
            // The blind spot only on a day the van ran. Today before it leaves, or a day it sat
            // in the yard, has no load on the road — flagging that would accuse an empty truck.
            return cold.Judged && day.FirstIgnitionOn is not null
                ? new FleetCell("—", "no readings", true, false)
                : new FleetCell("—", null, false, false);
        }

        var range = Range(cold.MinC, cold.MaxC);

        if (!cold.Judged)
        {
            return new FleetCell(range, "no limits set", false, false);
        }

        return cold.Breached
            ? new FleetCell(range, Duration(cold.MinutesOutside ?? 0) + " outside " + cold.LimitsLabel, true, false)
            : new FleetCell(range, "in range", false, false);
    }

    /// <summary>"1h 05m", "37m". Never "0m" — a caller with nothing to say has already said so.</summary>
    public static string Duration(int minutes) =>
        minutes < 60
            ? minutes.ToString(Invariant) + "m"
            : (minutes / 60).ToString(Invariant) + "h " + (minutes % 60).ToString("00", Invariant) + "m";

    private static string Litres(decimal litres) => litres.ToString("N1", Invariant) + " L";

    private static string Range(decimal? min, decimal? max) =>
        min is { } low && max is { } high
            ? low.ToString("0.0", Invariant) + " … " + high.ToString("0.0", Invariant) + " °C"
            : "—";

    private static string? Fills(int count, decimal litres) =>
        count == 0
            ? null
            : count == 1
                ? "1 fill · " + litres.ToString("N0", Invariant) + " L"
                : count.ToString(Invariant) + " fills · " + litres.ToString("N0", Invariant) + " L";
}
