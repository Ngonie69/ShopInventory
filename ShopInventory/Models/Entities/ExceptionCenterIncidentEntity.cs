using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

[Index(nameof(Source), nameof(OccurredAtUtc))]
[Index(nameof(Category), nameof(Status))]
public class ExceptionCenterIncidentEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Source { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Reference { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "RequiresReview";

    [MaxLength(50)]
    public string? SourceSystem { get; set; }

    [MaxLength(50)]
    public string? Provider { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public int RetryCount { get; set; }

    public int MaxRetries { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? OccurredAtUtc { get; set; }

    public DateTime? NextRetryAtUtc { get; set; }

    public bool CanRetry { get; set; }

    public string? DetailsJson { get; set; }
}