using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Index(nameof(CrateTransactionId), nameof(SubmissionRole), IsUnique = true)]
[Table("CratePodSubmissions")]
public class CratePodSubmissionEntity
{
    [Key]
    public int Id { get; set; }

    public int CrateTransactionId { get; set; }

    [Required]
    [MaxLength(20)]
    public string SubmissionRole { get; set; } = string.Empty;

    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Crate quantity cannot be negative")]
    public decimal Quantity { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    public Guid? SubmittedByUserId { get; set; }

    [ForeignKey(nameof(CrateTransactionId))]
    public CrateTransactionEntity CrateTransaction { get; set; } = null!;

    [ForeignKey(nameof(SubmittedByUserId))]
    public User? SubmittedByUser { get; set; }
}