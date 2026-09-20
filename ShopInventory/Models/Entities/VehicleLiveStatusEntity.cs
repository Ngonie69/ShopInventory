using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Where a vehicle is now, and how cold its box is — one row per vehicle, overwritten each poll.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot, deliberately not append-only. The history lives in
/// <see cref="VehicleTemperatureSampleEntity"/> and <see cref="VehicleDayRollupEntity"/>; this
/// table answers one question, for today, and keeping every poll would be a second copy of the
/// same series at a coarser resolution.
/// </para>
/// <para>
/// <b><see cref="PolledAtUtc"/> is not <see cref="EventAtUtc"/>, and the difference is the
/// point.</b> The first says when we asked; the second says when the vehicle last spoke. A van
/// parked out of coverage keeps a fresh poll time and a stale event time, and a screen that
/// showed the position without the age would be asserting that a truck is somewhere it left
/// hours ago. Everything that renders this must state the age.
/// </para>
/// <para>
/// One call to the provider fills this for the whole fleet, and it already carries all four
/// temperature channels — so a live view costs nothing beyond the poll itself.
/// </para>
/// </remarks>
[Index(nameof(RegistrationNormalized), IsUnique = true)]
public class VehicleLiveStatusEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(30)]
    public string RegistrationNormalized { get; set; } = null!;

    /// <summary>When the vehicle last reported. This is what "last seen" means.</summary>
    public DateTime? EventAtUtc { get; set; }

    /// <summary>When we last asked. Fresh even when the vehicle has been silent for days.</summary>
    public DateTime PolledAtUtc { get; set; } = DateTime.UtcNow;

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>The provider's own reverse geocode, so a position can be read without a map.</summary>
    [MaxLength(300)]
    public string? PositionDescription { get; set; }

    public int? SpeedKph { get; set; }

    public int? Bearing { get; set; }

    public bool? IgnitionOn { get; set; }

    public bool? Idling { get; set; }

    public long? OdometerMetres { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? Temp1C { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? Temp2C { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? Temp3C { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? Temp4C { get; set; }

    /// <summary>The driver the tracker associates with the vehicle, when a tag was used.</summary>
    [MaxLength(120)]
    public string? DriverName { get; set; }
}
