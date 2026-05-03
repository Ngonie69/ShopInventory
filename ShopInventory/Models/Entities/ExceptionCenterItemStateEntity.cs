using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Index(nameof(Source), nameof(ItemId), IsUnique = true)]
[Index(nameof(AssignedToUsername))]
public class ExceptionCenterItemStateEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Source { get; set; } = string.Empty;

    public int ItemId { get; set; }

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