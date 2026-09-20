using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// What one vehicle did on one trading day, as the tracker saw it.
/// </summary>
/// <remarks>
/// <para>
/// This is the row the departure compliance report joins to, and the reason the report can say
/// anything about a van that the rep's handset did not. It is a projection: every figure here is
/// rebuilt from the fleet API and nothing is authored, so a rebuild of any day is an upsert on
/// <c>(RegistrationNormalized, TradingDate)</c> and costs nothing.
/// </para>
/// <para>
/// <b>Nothing here overwrites what the rep recorded.</b> The handset's departure time and its two
/// odometer readings stay on <see cref="VanRouteDayEntity"/> untouched; these sit beside them so
/// the report can show both and name the difference. A projection that corrected the rep's
/// figures would destroy the discrepancy, which is the finding.
/// </para>
/// <para>
/// <b>Per-facet completeness.</b> The four <c>Has…</c> flags exist because a day can be built
/// while one endpoint is failing, and a row with no fuel figures has to be distinguishable from a
/// van with no fuel sensor and from a fuel call that errored. Without them the report cannot tell
/// "we did not ask" from "we asked and it said nothing", and those mean different things to a
/// supervisor.
/// </para>
/// </remarks>
[Index(nameof(RegistrationNormalized), nameof(TradingDate), IsUnique = true)]
[Index(nameof(TradingDate))]
public class VehicleDayRollupEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(30)]
    public string RegistrationNormalized { get; set; } = null!;

    /// <summary>
    /// The CAT calendar day, matching <see cref="VanRouteDayEntity.TradingDate"/> so a vehicle's
    /// day and a rep's day are the same day. Explicitly a <c>date</c>: the UTC convention would
    /// otherwise treat it as an instant and move it across midnight.
    /// </summary>
    [Column(TypeName = "date")]
    public DateTime TradingDate { get; set; }

    // — Departure ————————————————————————————————————————————————————

    /// <summary>
    /// The first time the key turned. Earlier than departure by however long the driver spent
    /// warming a diesel or pulling the box down, so this is recorded but is not by itself the
    /// verdict — see <see cref="FirstDepartureUtc"/>.
    /// </summary>
    public DateTime? FirstIgnitionOnUtc { get; set; }

    /// <summary>
    /// The first event that put the vehicle outside the depot radius of where the day's departure
    /// was recorded. This is what "the van left" means, and what the late-departure rule is
    /// measured from.
    /// </summary>
    public DateTime? FirstDepartureUtc { get; set; }

    public double? FirstDepartureLatitude { get; set; }

    public double? FirstDepartureLongitude { get; set; }

    public DateTime? LastIgnitionOffUtc { get; set; }

    /// <summary>How many times the key turned. A round of one long trip reads differently from twelve.</summary>
    public int? IgnitionCycleCount { get; set; }

    public int? DrivingSeconds { get; set; }

    public int? IdleSeconds { get; set; }

    // — Distance ——————————————————————————————————————————————————————

    public long? OdometerStartMetres { get; set; }

    public long? OdometerEndMetres { get; set; }

    /// <summary>
    /// Metres over the day, as the provider reports it rather than as the difference of the two
    /// readings above. Observed on this fleet: a vehicle can report a distance of zero with an end
    /// reading <em>below</em> its start, so the two are stored separately and never subtracted.
    /// </summary>
    public long? DistanceMetres { get; set; }

    /// <summary>The odometer was reset mid-day, so the distance across it means nothing.</summary>
    public bool OdometerWasReset { get; set; }

    /// <summary>The tracker was swapped, so the two readings are not one series.</summary>
    public bool TerminalChanged { get; set; }

    // — Fuel ——————————————————————————————————————————————————————————

    public decimal? FuelConsumedLitres { get; set; }

    public decimal? FuelLevelStartLitres { get; set; }

    public decimal? FuelLevelEndLitres { get; set; }

    public decimal? EstimatedFuelUsedLitres { get; set; }

    /// <summary>
    /// Whether the tank sender is calibrated. The provider states outright that an uncalibrated
    /// sensor produces no usable result, so a figure with this false is shown with the caveat
    /// rather than as a number.
    /// </summary>
    public bool? FuelIsCalibrated { get; set; }

    /// <summary>
    /// Whether the provider considers the two level readings settled. Recent readings come back
    /// false and are reprocessed later, so a figure taken today will move.
    /// </summary>
    public bool? FuelReadingsAccurate { get; set; }

    public int? FuelFillCount { get; set; }

    public decimal? FuelFilledLitres { get; set; }

    // — Temperature ————————————————————————————————————————————————————

    /// <summary>Which channel the breach figures below were evaluated against.</summary>
    public byte? TemperatureChannel { get; set; }

    public int? TemperatureSampleCount { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? TemperatureMinC { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? TemperatureMaxC { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? TemperatureAvgC { get; set; }

    public DateTime? TemperatureFirstSampleUtc { get; set; }

    public DateTime? TemperatureLastSampleUtc { get; set; }

    /// <summary>
    /// The limits this day was judged against, copied from the route at build time.
    /// </summary>
    /// <remarks>
    /// Snapshotted for the same reason <see cref="VanRouteDayEntity"/> snapshots its route's
    /// territory and truck: widening a route's limits this morning must not quietly erase last
    /// month's breach. Null means the round carried no limits and was not judged.
    /// </remarks>
    [Column(TypeName = "decimal(4,1)")]
    public decimal? LimitMinC { get; set; }

    /// <inheritdoc cref="LimitMinC"/>
    [Column(TypeName = "decimal(4,1)")]
    public decimal? LimitMaxC { get; set; }

    public int? MinutesAboveMaxLimit { get; set; }

    public int? MinutesBelowMinLimit { get; set; }

    /// <summary>Total minutes outside the limits, or null on a round with none set.</summary>
    [NotMapped]
    public int? MinutesOutsideLimits =>
        LimitMinC is null && LimitMaxC is null
            ? null
            : (MinutesAboveMaxLimit ?? 0) + (MinutesBelowMinLimit ?? 0);

    // — Provenance ————————————————————————————————————————————————————

    public bool HasActivity { get; set; }

    public bool HasOdometer { get; set; }

    public bool HasFuel { get; set; }

    public bool HasTemperature { get; set; }

    /// <summary>Whether every facet reported, which is what lets the checkpoint move past this day.</summary>
    [NotMapped]
    public bool IsComplete => HasActivity && HasOdometer && HasFuel && HasTemperature;

    public DateTime BuiltAtUtc { get; set; } = DateTime.UtcNow;

    public int AttemptCount { get; set; }

    /// <summary>Why a facet is missing, when one is. Shown to an administrator, not to a supervisor.</summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }
}
