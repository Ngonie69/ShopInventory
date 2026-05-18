using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Index(nameof(CrateTransactionId), IsUnique = true)]
[Table("CrateGrvs")]
public class CrateGrvEntity
{
    [Key]
    public int Id { get; set; }

    public int CrateTransactionId { get; set; }

    [MaxLength(30)]
    public string? GrvNumber { get; set; }

    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Expected crate quantity cannot be negative")]
    public decimal ExpectedQuantity { get; set; }

    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Actual crate quantity cannot be negative")]
    public decimal ActualQuantity { get; set; }

    [Precision(18, 2)]
    public decimal VarianceQuantity { get; set; }

    [Required]
    [MaxLength(20)]
    public string Direction { get; set; } = string.Empty;

    [Required]
    [MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "Open";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid? CreatedByUserId { get; set; }

    [ForeignKey(nameof(CrateTransactionId))]
    public CrateTransactionEntity CrateTransaction { get; set; } = null!;

    [ForeignKey(nameof(CreatedByUserId))]
    public User? CreatedByUser { get; set; }
}