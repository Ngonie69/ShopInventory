using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Index(nameof(Source), nameof(ItemKey), IsUnique = true)]
[Index(nameof(Source), nameof(ItemId))]
[Index(nameof(AssignedToUsername))]
public class ExceptionCenterItemStateEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The int primary key of the item, for the sources that have one; zero for Guid-keyed
    /// sources. Retained because every state written before Guid sources existed identifies its
    /// item by this alone — <see cref="ItemKey"/> is what acknowledge, assign and retry route on.
    /// </summary>
    public int ItemId { get; set; }

    /// <summary>
    /// Identifies the item within its source, whatever the shape of that source's primary key:
    /// the decimal id for int-keyed sources, the "D" formatted Guid for Guid-keyed ones.
    /// Nullable only so the column could be added ahead of its backfill; every row carries one.
    /// </summary>
    [MaxLength(100)]
    public string? ItemKey { get; set; }

    public bool IsAcknowledged { get; set; }

    public DateTime? AcknowledgedAtUtc { get; set; }

    public Guid? AcknowledgedByUserId { get; set; }

    [MaxLength(50)]
    public string? AcknowledgedByUsername { get; set; }

    public Guid? AssignedToUserId { get; set; }

    [MaxLength(50)]
    public string? AssignedToUsername { get; set; }

    public DateTime? AssignedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}