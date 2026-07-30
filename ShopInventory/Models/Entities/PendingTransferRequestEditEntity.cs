using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A change to an SAP transfer request that has been proposed but not written to SAP, because
/// the editor is not assigned the warehouse the stock would leave. It is applied to SAP only
/// once the approval process completes.
/// </summary>
[Index(nameof(Status), nameof(CreatedAtUtc))]
[Index(nameof(RequestDocEntry))]
[Index(nameof(CreatedByUserId))]
public sealed class PendingTransferRequestEditEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>SAP DocEntry of the transfer request being changed.</summary>
    public int RequestDocEntry { get; set; }

    public int RequestDocNum { get; set; }

    [Required]
    [MaxLength(50)]
    public string FromWarehouse { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string ToWarehouse { get; set; } = string.Empty;

    /// <summary>
    /// The source warehouse the change reassigns the request to, or <c>null</c> when it leaves
    /// <see cref="FromWarehouse"/> alone. Held separately so a reviewer sees both ends of the move.
    /// </summary>
    [MaxLength(50)]
    public string? ProposedFromWarehouse { get; set; }

    /// <summary>
    /// The destination warehouse the change reassigns the request to, or <c>null</c> when it leaves
    /// <see cref="ToWarehouse"/> alone.
    /// </summary>
    [MaxLength(50)]
    public string? ProposedToWarehouse { get; set; }

    /// <summary>
    /// JSON serialised <c>TransferRequestEditLine</c> list: the lines the request should be left
    /// with. Lines absent from it are the ones being removed.
    /// </summary>
    [Required]
    public string ProposedLinesJson { get; set; } = string.Empty;

    /// <summary>The lines as they stood when the edit was proposed, for reviewer comparison.</summary>
    [Required]
    public string OriginalLinesJson { get; set; } = string.Empty;

    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = PendingTransferRequestEditStatuses.AwaitingApproval;

    public Guid CreatedByUserId { get; set; }

    [Required]
    [MaxLength(150)]
    public string CreatedByName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? CreatedByRole { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(500)]
    public string? Reason { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public DateTime? DecidedAtUtc { get; set; }

    public DateTime? AppliedAtUtc { get; set; }

    /// <summary>Populated when the approved change could not be written to SAP. Retryable.</summary>
    [MaxLength(2000)]
    public string? LastError { get; set; }
}

public static class PendingTransferRequestEditStatuses
{
    /// <summary>Proposed and waiting on one or more approval stages.</summary>
    public const string AwaitingApproval = "AwaitingApproval";

    /// <summary>Rejected by an authorizer; will never reach SAP.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Approved and written to the SAP transfer request.</summary>
    public const string Applied = "Applied";

    /// <summary>Approved, but writing to SAP failed. Retryable.</summary>
    public const string ApplyFailed = "ApplyFailed";

    /// <summary>Withdrawn by the proposer before a decision was made.</summary>
    public const string Cancelled = "Cancelled";
}
