using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Broken or damaged stock a van rep collected from a shop, as reported from the handset.
/// </summary>
/// <remarks>
/// <para>
/// The rep swaps the shop's broken units for good ones off the van and carries the broken ones back,
/// so the van is physically short by what it collected while SAP still counts it as sellable stock.
/// Nothing moves in SAP when the rep reports: the office counts what actually came off the van,
/// confirms the quantities, and only then is a stock transfer posted from the van's warehouse into
/// the returns warehouse.
/// </para>
/// <para>
/// <see cref="MarketBreakageLineEntity.ReportedQuantity"/> is kept as the rep sent it and never
/// overwritten; the office's count is <see cref="MarketBreakageLineEntity.ConfirmedQuantity"/>, and
/// the transfer is built from that alone.
/// </para>
/// </remarks>
[Index(nameof(Status), nameof(CreatedAtUtc))]
[Index(nameof(ReportedByUserId), nameof(CreatedAtUtc))]
[Index(nameof(VanWarehouseCode))]
public sealed class MarketBreakageEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Generated on the handset per report, so a submit retried after a lost reply finds the
    /// report it already made instead of making a second one.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ClientRequestId { get; set; } = string.Empty;

    public Guid ReportedByUserId { get; set; }

    [Required]
    [MaxLength(150)]
    public string ReportedByName { get; set; } = string.Empty;

    /// <summary>
    /// The rep's van at the time of the report — the warehouse the transfer takes the stock from.
    /// Snapshotted rather than read from the account at confirmation, because a rep can be moved
    /// to another van before the office gets to the report.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string VanWarehouseCode { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? CardCode { get; set; }

    [MaxLength(200)]
    public string? CardName { get; set; }

    [MaxLength(500)]
    public string? Remarks { get; set; }

    /// <summary>When the rep recorded it on the handset (UTC).</summary>
    public DateTime CapturedAtUtc { get; set; }

    /// <summary>When the server received it (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = MarketBreakageStatuses.Pending;

    public Guid? DecidedByUserId { get; set; }

    [MaxLength(150)]
    public string? DecidedByName { get; set; }

    public DateTime? DecidedAtUtc { get; set; }

    [MaxLength(500)]
    public string? DecisionRemarks { get; set; }

    [MaxLength(50)]
    public string? ReturnsWarehouseCode { get; set; }

    public int? SapDocEntry { get; set; }

    public int? SapDocNum { get; set; }

    public DateTime? TransferredAtUtc { get; set; }

    public DateTime? LastAttemptedAtUtc { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public List<MarketBreakageLineEntity> Lines { get; set; } = [];
}

/// <summary>
/// Where a breakage report is in its life.
/// </summary>
public static class MarketBreakageStatuses
{
    /// <summary>Reported from the handset; nothing has moved in SAP.</summary>
    public const string Pending = "Pending";

    /// <summary>
    /// Confirmed, and the transfer is on its way to SAP. Only ever transient: a report read back in
    /// this state was stranded mid-post (a crash, a restart), and confirming it again is how it is
    /// finished — the post lock refuses that while a post is genuinely still running.
    /// </summary>
    public const string Transferring = "Transferring";

    /// <summary>Confirmed and the stock transfer into the returns warehouse is in SAP.</summary>
    public const string Transferred = "Transferred";

    /// <summary>
    /// Confirmed, but SAP refused or did not answer the transfer. Retryable — confirming again
    /// posts it again, with whatever quantities the office then enters.
    /// </summary>
    public const string TransferFailed = "TransferFailed";

    /// <summary>The office turned it down. Nothing moved and nothing will.</summary>
    public const string Rejected = "Rejected";

    public static readonly IReadOnlyList<string> All = [Pending, Transferring, Transferred, TransferFailed, Rejected];

    /// <summary>The states a confirm may start from, including a post that was stranded.</summary>
    public static bool MayConfirm(string status) => status is Pending or TransferFailed or Transferring;

    /// <summary>
    /// The states a reject may start from. Not <see cref="Transferring"/>: that transfer may already
    /// be in SAP, and a rejected report whose stock moved is a record that lies.
    /// </summary>
    public static bool MayReject(string status) => status is Pending or TransferFailed;
}
