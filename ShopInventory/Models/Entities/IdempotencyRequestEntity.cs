using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models.Entities;

public class IdempotencyRequestEntity
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(120)]
    public string Scope { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string RequestHash { get; set; } = string.Empty;

    public IdempotencyRequestStatus Status { get; set; } = IdempotencyRequestStatus.InProgress;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public string? ResponsePayload { get; set; }
}