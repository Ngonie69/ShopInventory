using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One SAP stock transfer into one till's warehouse that the tills there have been told about, or
/// were deliberately not told about. See
/// <see cref="Features.InventoryTransfers.Commands.AnnounceTransferArrivals.AnnounceTransferArrivalsHandler"/>.
/// </summary>
/// <remarks>
/// The unique index is the whole of the deduplication: a pass inserts the rows before it sends, so a
/// transfer is announced once however many passes, or nodes, see it.
/// </remarks>
[Index(nameof(TransferDocEntry), nameof(WarehouseCode), IsUnique = true)]
public class TransferArrivalNoticeEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The transfer's DocEntry, or <see cref="StartMarkerDocEntry"/> on the one row that records when
    /// watching began.
    /// </summary>
    public int TransferDocEntry { get; set; }

    public int TransferDocNum { get; set; }

    /// <summary>The till warehouse the stock moved into, trimmed and upper-cased.</summary>
    [Required]
    [MaxLength(50)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>Where the stock came from; several, comma-separated, when the lines name more than one.</summary>
    [MaxLength(200)]
    public string? SourceWarehouse { get; set; }

    public int ItemCount { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the tills were sent it. Null for a transfer that was already in SAP when watching began,
    /// which is recorded so it is never announced.
    /// </summary>
    public DateTime? AnnouncedAt { get; set; }

    /// <summary>
    /// The DocEntry of the row written by the very first pass. SAP numbers documents from 1, so it is
    /// never a real transfer's.
    /// </summary>
    public const int StartMarkerDocEntry = 0;
}
