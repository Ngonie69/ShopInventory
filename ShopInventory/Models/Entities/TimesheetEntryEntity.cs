using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

public class TimesheetEntryEntity
{
    [Key]
    public int Id { get; set; }

    public Guid UserId { get; set; }

    [Required]
    [MaxLength(50)]
    public required string Username { get; set; }

    [Required]
    [MaxLength(50)]
    public required string CustomerCode { get; set; }

    [Required]
    [MaxLength(200)]
    public required string CustomerName { get; set; }

    public DateTime CheckInTime { get; set; }

    public DateTime? CheckOutTime { get; set; }

    public double? CheckInLatitude { get; set; }

    public double? CheckInLongitude { get; set; }

    public double? CheckOutLatitude { get; set; }

    public double? CheckOutLongitude { get; set; }

    [MaxLength(500)]
    public string? CheckInNotes { get; set; }

    [MaxLength(500)]
    public string? CheckOutNotes { get; set; }

    /// <summary>
    /// Duration in minutes, computed on check-out.
    /// </summary>
    public double? DurationMinutes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }
}
