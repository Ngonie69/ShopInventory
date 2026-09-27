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

    /// <summary>
    /// When a post to SAP was last started. Stamped before the attempt runs, not after it
    /// resolves, so an attempt that timed out or crashed still leaves a record that it happened —
    /// which is the case where knowing someone already tried matters most.
    /// Null on records created before this was added, and on drafts nobody has tried to post.
    /// </summary>
    public DateTime? LastAttemptedAtUtc { get; set; }

    /// <summary>Populated when posting to SAP failed after the approval completed.</summary>
    [MaxLength(2000)]
    public string? LastError { get; set; }

    /// <summary>
    /// The lines left out when only the in-stock part of the transfer was posted, as a JSON list of
    /// <c>PendingTransferDroppedLine</c>. Null on every transfer that posted whole.
    /// </summary>
    /// <remarks>
    /// <see cref="PayloadJson"/>, <see cref="LineCount"/> and <see cref="TotalQuantity"/> describe
    /// what reached SAP once this is set, so this column is the only record of what the van asked
    /// for and did not get.
    /// </remarks>
    public string? DroppedLinesJson { get; set; }

    /// <summary>
    /// When an approved transfer that failed to post was withdrawn instead of retried. Kept apart
    /// from <see cref="DecidedAtUtc"/>, which still says when the approval completed — overwriting
    /// it would make the decision look days slower than it was.
    /// </summary>
    public DateTime? WithdrawnAtUtc { get; set; }

    public Guid? WithdrawnByUserId { get; set; }

    [MaxLength(500)]
    public string? WithdrawalReason { get; set; }

    /// <summary>
    /// True when the SAP document was found in SAP and recorded against this transfer by a person,
    /// rather than created by this system's own post — the case after a post timed out and SAP had
    /// in fact created the transfer.
    /// </summary>
    public bool PostRecordedManually { get; set; }
}

/// <summary>A line left out of a partial post, with what the depot had when it was left out.</summary>
public sealed record PendingTransferDroppedLine(
    string ItemCode,
    decimal RequestedQuantity,
    decimal AvailableQuantity,
    string? BatchNumber);

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

    /// <summary>
    /// Withdrawn: by the originator before a decision was made, or — with
    /// <see cref="PendingInventoryTransferEntity.WithdrawnAtUtc"/> set — after it was approved and
    /// failed to post, by someone who decided it should not be retried.
    /// </summary>
    public const string Cancelled = "Cancelled";
}
