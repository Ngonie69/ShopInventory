using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Which single handset may sign receipts offline on a fiscal device.
///
/// It exists because a fiscal device is one receipt chain and not a pool. Every receipt is signed over the
/// hash of the receipt before it, so the signer has to know what came last — which is only true while
/// exactly one signer is active. Two handsets out of coverage on one device do not produce two streams of
/// receipts; they produce one forked chain, and FDMS refuses the whole fiscal day when the file is
/// uploaded, after every customer has gone home with a printed receipt.
///
/// Allocating each handset its own block of receipt numbers does not avoid this, which is the trap worth
/// naming: a handset holding 201-300 still has to chain its first receipt onto the hash of number 200,
/// and that receipt is on another handset which is still out of signal.
///
/// So offline signing is nominated rather than shared. One row per device, naming at most one holder.
/// </summary>
[Table("FiscalDeviceOfflineLeases")]
public class FiscalDeviceOfflineLeaseEntity
{
    /// <summary>The ZIMRA device this nomination governs. One row per device.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int DeviceId { get; set; }

    /// <summary>
    /// The handset user allowed to sign offline on this device, or null when no one is.
    /// </summary>
    /// <remarks>
    /// Null is the safe state and the default: with no nomination every handset is refused a lease and
    /// trades online only, which costs sales in dead spots but cannot corrupt a fiscal day.
    /// </remarks>
    public Guid? HolderUserId { get; set; }

    /// <summary>
    /// The holder's name as the office would say it, kept here so a refused handset can tell its rep who
    /// to call without the refusal path having to join back to Users.
    /// </summary>
    [MaxLength(256)]
    public string? HolderLabel { get; set; }

    public DateTime? AssignedAtUtc { get; set; }

    public Guid? AssignedByUserId { get; set; }

    [MaxLength(256)]
    public string? AssignedByName { get; set; }

    /// <summary>
    /// Signed receipts the holder said it had not yet uploaded, as of <see cref="HolderLastSeenAtUtc"/>.
    /// Null when it has never said — which is not the same as none, and is not treated as none.
    /// </summary>
    /// <remarks>
    /// The one fact that makes a handover safe to judge. The server cannot see what a handset is still
    /// carrying, so the handset reports it whenever it collects a lease, and the count is remembered here.
    /// Moving the nomination while this is above zero strands those receipts: the next holder starts from
    /// a position that does not account for them, and signs over numbers that are already spent.
    ///
    /// Nullable because a handset too old to report would otherwise read as an empty queue and make a
    /// handover look safe at exactly the moment it is not. Unknown blocks the handover the same way a
    /// non-zero count does, and the office can still force it deliberately.
    ///
    /// Not a lock. It is the difference between an office that reassigns knowingly and one that finds out
    /// at the end of the day.
    /// </remarks>
    public int? HolderPendingSales { get; set; }

    /// <summary>When the holder last collected a lease, which is also when it last reported its queue.</summary>
    public DateTime? HolderLastSeenAtUtc { get; set; }

    /// <summary>When the holder last confirmed it had nothing left to upload.</summary>
    public DateTime? ReleasedAtUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this device currently has a handset nominated to sign offline.</summary>
    public bool IsHeld => HolderUserId is not null;

    /// <summary>
    /// Whether the nomination can be moved without stranding signed receipts — either nobody holds it, or
    /// the holder has reported an empty queue.
    /// </summary>
    /// <remarks>
    /// A holder that has never reported (<see cref="HolderPendingSales"/> null) is treated as unsafe to
    /// hand over, not safe. The office can still move it deliberately; what it cannot do is move it by
    /// accident because an old handset said nothing.
    /// </remarks>
    public bool CanHandOver => HolderUserId is null || HolderPendingSales == 0;
}
