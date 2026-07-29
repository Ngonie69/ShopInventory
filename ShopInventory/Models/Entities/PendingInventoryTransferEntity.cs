using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A direct inventory transfer that has been submitted but is held locally until the
/// approval process completes. Nothing is posted to SAP until every stage approves.
/// </summary>
[Index(nameof(Status), nameof(CreatedAtUtc))]
[Index(nameof(FromWarehouse))]
[Index(nameof(CreatedByUserId))]
public sealed class PendingInventoryTransferEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Idempotency key supplied by the caller, when present.
    /// </summary>
    [MaxLength(200)]
    public string? ClientRequestId { get; set; }

    /// <summary>
    /// Human-readable reference for the held draft, assigned at submission — the transfer has
    /// no SAP DocNum until it posts, so this is the only number anyone can quote for it.
    /// Null only on records created before draft numbering existed.
    /// </summary>
    [MaxLength(30)]
    public string? DraftNumber { get; set; }

    [Required]
    [MaxLength(50)]
    public string FromWarehouse { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string ToWarehouse { get; set; } = string.Empty;

    /// <summary>
    /// JSON serialised CreateInventoryTransferRequest, replayed against SAP on approval.
    /// </summary>
    [Required]
    public string PayloadJson { get; set; } = string.Empty;

    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = PendingInventoryTransferStatuses.AwaitingApproval;

    public Guid CreatedByUserId { get; set; }

    [Required]
    [MaxLength(150)]
    public string CreatedByName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? CreatedByRole { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The approval request driving this transfer.
    /// </summary>
    public Guid? ApprovalRequestId { get; set; }

    public int LineCount { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal TotalQuantity { get; set; }

    [MaxLength(500)]
    public string? Comments { get; set; }

    public DateTime? DocDate { get; set; }

    public DateTime? DueDate { get; set; }

    /// <summary>SAP DocEntry once posted.</summary>
    public int? SapDocEntry { get; set; }

    /// <summary>SAP DocNum once posted.</summary>
    public int? SapDocNum { get; set; }

    public DateTime? PostedAtUtc { get; set; }

    public Guid? PostedByUserId { get; set; }

    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>Populated when posting to SAP failed after the approval completed.</summary>
    [MaxLength(2000)]
    public string? LastError { get; set; }
}

public static class PendingInventoryTransferStatuses
{
    /// <summary>Submitted and waiting on one or more approval stages.</summary>
    public const string AwaitingApproval = "AwaitingApproval";

    /// <summary>Fully approved but not yet successfully posted to SAP.</summary>
    public const string Approved = "Approved";

    /// <summary>Rejected by an authorizer; will never post.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Posted to SAP; <see cref="PendingInventoryTransferEntity.SapDocEntry"/> is set.</summary>
    public const string Posted = "Posted";

    /// <summary>Approved, but the SAP post failed. Retryable.</summary>
    public const string PostFailed = "PostFailed";

    /// <summary>Withdrawn by the originator before a decision was made.</summary>
    public const string Cancelled = "Cancelled";
}
