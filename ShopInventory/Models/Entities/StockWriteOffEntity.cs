using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Stock written off a warehouse: counted, given a reason, and issued out of SAP so the books stop
/// carrying product that no longer exists.
/// </summary>
/// <remarks>
/// <para>
/// The record exists rather than the SAP document alone for three reasons: it is the guard that stops
/// one write-off posting twice, it is where a refusal or a timeout is left so somebody can act on it,
/// and it keeps who wrote the stock off and why after the SAP document has been archived or
/// cancelled.
/// </para>
/// <para>
/// A line is one thing counted — an item, and the batch it came out of if the item is batch-managed.
/// Five units of one batch and three of another are two lines, which is how they were counted and
/// how SAP records them.
/// </para>
/// </remarks>
[Index(nameof(Status), nameof(CreatedAtUtc))]
[Index(nameof(WarehouseCode), nameof(CreatedAtUtc))]
[Index(nameof(RaisedByUserId), nameof(CreatedAtUtc))]
public sealed class StockWriteOffEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Supplied per attempt by the caller, so a submit retried after a lost reply finds the write-off
    /// it already raised instead of raising a second one.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ClientRequestId { get; set; } = string.Empty;

    public Guid RaisedByUserId { get; set; }

    [Required]
    [MaxLength(150)]
    public string RaisedByName { get; set; } = string.Empty;

    /// <summary>The warehouse the stock leaves. One per write-off: a goods issue has one side.</summary>
    [Required]
    [MaxLength(50)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// Why the stock is being written off. Held on the header because that is how it is decided, and
    /// copied onto every line on the way to SAP because SAP keeps the reason on the line.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Remarks { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = StockWriteOffStatuses.Pending;

    /// <summary>The reference written into the SAP document, for finding it again after a lost reply.</summary>
    [MaxLength(100)]
    public string? SapReference { get; set; }

    public int? SapDocEntry { get; set; }

    public int? SapDocNum { get; set; }

    public DateTime? PostedAtUtc { get; set; }

    public DateTime? LastAttemptedAtUtc { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public List<StockWriteOffLineEntity> Lines { get; set; } = [];
}

/// <summary>
/// Where a write-off is in its life.
/// </summary>
public static class StockWriteOffStatuses
{
    /// <summary>Raised and nothing has moved in SAP.</summary>
    public const string Pending = "Pending";

    /// <summary>
    /// The goods issue is on its way to SAP. Only ever transient: a write-off read back in this state
    /// was stranded mid-post by a crash or a restart, and posting it again is how it is finished —
    /// the post lock refuses that while a post is genuinely still running.
    /// </summary>
    public const string Posting = "Posting";

    /// <summary>The goods issue is in SAP and the stock has left.</summary>
    public const string Posted = "Posted";

    /// <summary>
    /// SAP refused the goods issue, or did not answer it. Retryable — posting again sends it again.
    /// </summary>
    public const string PostFailed = "PostFailed";

    /// <summary>Abandoned before anything moved. Nothing moved and nothing will.</summary>
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = [Pending, Posting, Posted, PostFailed, Cancelled];

    /// <summary>The states a post may start from, including a post that was stranded.</summary>
    public static bool MayPost(string status) => status is Pending or PostFailed or Posting;

    /// <summary>
    /// The states a cancel may start from. Not <see cref="Posting"/>: that goods issue may already be
    /// in SAP, and a cancelled write-off whose stock left is a record that lies.
    /// </summary>
    public static bool MayCancel(string status) => status is Pending or PostFailed;
}
