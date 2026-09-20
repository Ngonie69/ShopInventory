using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One temperature reading from one probe on one vehicle — the cold-chain evidence trail.
/// </summary>
/// <remarks>
/// <para>
/// This is the only table here that is not a projection, because it is the only one that cannot
/// be rebuilt. The fleet provider keeps temperature for about two months; a food business is
/// asked to show a load stayed cold long after that. Once a sample ages out of their system,
/// this row is the only record that it was ever taken.
/// </para>
/// <para>
/// <b>Always reasoned about on <see cref="EventAtUtc"/>, never on
/// <see cref="ReceivedAtUtc"/>.</b> A van out of coverage reports hours late, and a reading
/// filed at the time it arrived would put a warm hour in the wrong part of the day — or in the
/// wrong day entirely.
/// </para>
/// <para>
/// Volume, stated so it is not a surprise: roughly one reading every few minutes per fitted
/// probe, so a few thousand rows a day across a fleet this size and a few million a year. That
/// is unremarkable for Postgres, and only channels that actually report are written — three of
/// the four channels on this fleet never do.
/// </para>
/// </remarks>
[Index(nameof(RegistrationNormalized), nameof(Channel), nameof(EventAtUtc), IsUnique = true)]
[Index(nameof(RegistrationNormalized), nameof(TradingDate))]
public class VehicleTemperatureSampleEntity
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(30)]
    public string RegistrationNormalized { get; set; } = null!;

    /// <summary>The CAT day this reading belongs to, derived from <see cref="EventAtUtc"/>.</summary>
    [Column(TypeName = "date")]
    public DateTime TradingDate { get; set; }

    /// <summary>Which of the tracker's four probes this is. Only channel 1 reports on this fleet.</summary>
    public byte Channel { get; set; }

    /// <summary>When the reading was taken. The unique key, with the registration and channel.</summary>
    public DateTime EventAtUtc { get; set; }

    /// <summary>
    /// When it reached the provider. Kept only to show how late a van was reporting; never used
    /// to place a reading in time.
    /// </summary>
    public DateTime? ReceivedAtUtc { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal TemperatureC { get; set; }
}
