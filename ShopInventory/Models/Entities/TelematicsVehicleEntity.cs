using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A vehicle as the fleet provider knows it, and what its tracker can actually measure.
/// </summary>
/// <remarks>
/// <para>
/// Cached rather than read live for two reasons: the registration picker on <c>/van-sales/routes</c>
/// needs a list without a round trip to an external API, and the rollup needs to know which fuel
/// calls are worth making at all. On the fleet as it stands, three of the four vehicles have no
/// fuel sensor of any kind, so skipping them removes most of the rollup's per-vehicle cost.
/// </para>
/// <para>
/// Everything here except <see cref="HasTemperatureProbe"/> and
/// <see cref="LastTemperatureSeenAtUtc"/> is owned by the provider and overwritten on every sync.
/// Those two are inferred from readings actually arriving, because the fleet API publishes
/// capability flags for fuel and electric and says nothing whatever about the four temperature
/// channels — "has a probe" can only honestly mean "has reported one".
/// </para>
/// </remarks>
[Index(nameof(RegistrationNormalized), IsUnique = true)]
[Index(nameof(CartrackVehicleId))]
public class TelematicsVehicleEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The registration reduced to letters and digits, upper case — the key everything joins on.
    /// See <c>TelematicsRegistration</c> for why this exists rather than the plate as typed.
    /// </summary>
    [Required]
    [MaxLength(30)]
    public string RegistrationNormalized { get; set; } = null!;

    /// <summary>The plate as the provider spells it, for showing a person.</summary>
    [MaxLength(30)]
    public string? Registration { get; set; }

    public long? CartrackVehicleId { get; set; }

    [MaxLength(40)]
    public string? TerminalSerial { get; set; }

    /// <summary>
    /// The customer's own name for the vehicle. This fleet uses a number joined to the plate —
    /// "306_AFQ9644" — which is <b>not</b> a registration and must never be matched as one.
    /// </summary>
    [MaxLength(100)]
    public string? ClientVehicleName { get; set; }

    [MaxLength(60)]
    public string? Manufacturer { get; set; }

    [MaxLength(60)]
    public string? Model { get; set; }

    /// <summary>
    /// In the workshop. This is the <em>reason</em> a day has no telematics, and the report says
    /// so rather than reporting a van that sat still.
    /// </summary>
    public bool IsUnderMaintenance { get; set; }

    /// <inheritdoc cref="IsUnderMaintenance"/>
    public bool TerminalInRepair { get; set; }

    /// <summary>The engine reports its own consumption. None of this fleet does.</summary>
    public bool HasFuelCanbusConsumed { get; set; }

    /// <inheritdoc cref="HasFuelCanbusConsumed"/>
    public bool HasFuelCanbusLevel { get; set; }

    /// <summary>A tank sender is fitted. One of the four vehicles has one.</summary>
    public bool HasFuelAnalogLevel { get; set; }

    /// <summary>Whether any fuel figure is worth asking for at all.</summary>
    [NotMapped]
    public bool HasAnyFuelSensor => HasFuelCanbusConsumed || HasFuelCanbusLevel || HasFuelAnalogLevel;

    /// <summary>
    /// Whether a temperature reading has ever arrived. Inferred, never told by the API — see the
    /// class remarks. Null means no sync has looked yet, which is not the same as no probe.
    /// </summary>
    public bool? HasTemperatureProbe { get; set; }

    /// <inheritdoc cref="HasTemperatureProbe"/>
    public DateTime? LastTemperatureSeenAtUtc { get; set; }

    /// <summary>
    /// Still listed by the provider. A vehicle that leaves the fleet is retired rather than
    /// deleted, so last month's rows keep saying which truck ran the round instead of turning
    /// into "not in the fleet".
    /// </summary>
    public bool IsActiveInFleet { get; set; } = true;

    public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}
