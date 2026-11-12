using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One change an Admin made to the odometer readings on a rep's trading day: what the day said
/// before, what it says now, who changed it and why.
/// </summary>
/// <remarks>
/// <para>
/// The readings on <see cref="VanRouteDayEntity"/> are typed on the handset, and a slip of the thumb
/// (an extra digit, the trip meter instead of the odometer, the other truck's dashboard) makes the
/// day's distance wrong, or blocks the rep from ending the day at all when the morning reading is
/// higher than the evening one. The day is corrected in place, because every report reads the
/// readings from the day, and this row is what is left to show it was.
/// </para>
/// <para>
/// Append-only. The mileage is a control: the fleet audit holds it against the tracker, so a
/// correction that left no trace would let a discrepancy be quietly erased. The first row for a
/// reading holds what the rep actually typed, however many times it is corrected after.
/// </para>
/// </remarks>
[Index(nameof(VanRouteDayId), nameof(CorrectedAt))]
public class VanRouteDayMileageCorrectionEntity
{
    [Key]
    public int Id { get; set; }

    public int VanRouteDayId { get; set; }

    [ForeignKey(nameof(VanRouteDayId))]
    public VanRouteDayEntity? VanRouteDay { get; set; }

    /// <summary>The odometer on leaving, as the day held it before this correction.</summary>
    public int? PreviousStartingMileage { get; set; }

    /// <summary>The odometer on returning, as the day held it before this correction.</summary>
    public int? PreviousClosingMileage { get; set; }

    /// <summary>The odometer on leaving, as this correction left it.</summary>
    public int? StartingMileage { get; set; }

    /// <summary>The odometer on returning, as this correction left it.</summary>
    public int? ClosingMileage { get; set; }

    /// <summary>Why the readings were changed, in the Admin's words. Required: a change with no reason is not a control.</summary>
    [Required]
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    public Guid? CorrectedByUserId { get; set; }

    /// <summary>The Admin's username at the time, kept so the trail survives the account.</summary>
    [MaxLength(100)]
    public string? CorrectedByName { get; set; }

    public DateTime CorrectedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public bool ChangedStartingMileage => PreviousStartingMileage != StartingMileage;

    [NotMapped]
    public bool ChangedClosingMileage => PreviousClosingMileage != ClosingMileage;
}
