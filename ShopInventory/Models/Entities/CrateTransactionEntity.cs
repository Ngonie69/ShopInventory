using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Index(nameof(InvoiceDocEntry), IsUnique = true)]
[Table("CrateTransactions")]
public class CrateTransactionEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(30)]
    public string TransactionType { get; set; } = string.Empty;

    public int? InvoiceDocEntry { get; set; }

    public int? InvoiceDocNum { get; set; }

    [Required]
    [MaxLength(50)]
    public string ShopCardCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ShopName { get; set; }

    [Precision(18, 2)]
    [Range(0, double.MaxValue, ErrorMessage = "Expected crate quantity cannot be negative")]
    public decimal ExpectedQuantity { get; set; }

    public DateTime EffectiveDate { get; set; } = DateTime.UtcNow.Date;

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    [ForeignKey(nameof(CreatedByUserId))]
    public User? CreatedByUser { get; set; }

    public ICollection<CratePodSubmissionEntity> PodSubmissions { get; set; } = new List<CratePodSubmissionEntity>();

    public CrateGrvEntity? Grv { get; set; }
}