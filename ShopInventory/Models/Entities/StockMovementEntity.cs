using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// What moved the ledger.
/// </summary>
public static class StockMovementKinds
{
    /// <summary>A document claimed units before it was posted, and could have been refused.</summary>
    public const string Commit = "Commit";

    /// <summary>A document that already exists reported what it took. Could not be refused.</summary>
    public const string Settle = "Settle";

    /// <summary>Units handed back — a document refused after its claim was taken, or reversed.</summary>
    public const string Release = "Release";
}

/// <summary>
/// One movement of the stock ledger: which document moved how much of an item in a warehouse, and
/// what was left there afterwards.
/// </summary>
/// <remarks>
/// <para><b>Why a row per movement.</b> <see cref="DailyStockSnapshotItemEntity.AvailableQuantity"/>
/// is a cell that several subsystems overwrite through the day. When it reads 3 against a morning
/// figure of 100, nothing could say where the 97 went: the movements existed only as
/// <c>ILogger</c> lines, which cannot be queried, do not survive, and cannot be counted. A
/// divergence could be recorded but never attributed.</para>
///
/// <para><b>Why the unique index.</b> It is the idempotency key, and it is the half of this that
/// prevents a wrong figure rather than merely explaining one. A command retried, a queue entry
/// re-run, or a job that commits what capture already committed all take the units twice today, and
/// there is nothing afterwards that can detect it — the ledger just goes quiet and short.
/// <c>StockTransferAdjustmentEntity</c> has had this protection for transfers all along, which is
/// why the listener can re-read a three-day window every cycle without double-counting; this is the
/// same guarantee for everything else that moves the ledger.</para>
///
/// <para><b><see cref="DocumentKey"/> is nullable on purpose.</b> Deduplication is opt-in, per call
/// site, and only where the caller genuinely has a stable identity for the document. A key that is
/// not unique across documents would be far worse than no key at all: the second real document
/// would silently not take its units. So a caller with nothing better passes null, the movement is
/// still journalled, and nothing is deduplicated — NULLs do not collide in a unique index.</para>
///
/// <para><b>One row per item and warehouse, not per batch.</b> That is the grain the ledger decides
/// at — a claim is aggregated to it before anything is checked — and so it is the only grain at
/// which "this document has already moved the ledger" is a well-formed statement. A document may
/// legitimately draw one item from several snapshot rows, expiry order deciding how the units are
/// spread; journalling each row separately would make a single claim collide with itself on the
/// index. The per-batch balances are still on the snapshot rows, which is where they belong.</para>
///
/// <para><b>The invariant.</b> For rows this ledger alone has touched,
/// <c>OriginalQuantity + Σ Quantity = AvailableQuantity</c>. It is not yet true system-wide: the
/// transfer webhook and the hourly reconciliation also write that cell and do not journal here yet.
/// When they do, this becomes a continuously checkable statement about the whole day, and any
/// writer that bypasses the journal breaks it visibly.</para>
/// </remarks>
[Index(nameof(LedgerDay))]
[Index(nameof(LedgerDay), nameof(ItemCode), nameof(WarehouseCode))]
[Index(
    nameof(LedgerDay),
    nameof(Kind),
    nameof(DocumentKey),
    nameof(ItemCode),
    nameof(WarehouseCode),
    IsUnique = true)]
public class StockMovementEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The ledger day this belongs to — the snapshot's day, not the calendar's. See
    /// <c>StockLedgerDay</c> for why those differ.
    /// </summary>
    [Column(TypeName = "date")]
    public DateTime LedgerDay { get; set; }

    /// <summary>One of <see cref="StockMovementKinds"/>.</summary>
    [Required]
    [MaxLength(20)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// A stable identity for the document, unique among documents of this <see cref="Kind"/> on this
    /// day, or null when the caller has none. Null disables deduplication for that movement.
    /// </summary>
    [MaxLength(100)]
    public string? DocumentKey { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// Signed: negative when units left, positive when they came back. Summing this column is the
    /// point of the table, so the sign has to live in the data rather than in the kind.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// What the item held in this warehouse once the movement was applied, across every batch row.
    /// Lets the journal and the snapshot be compared without replaying the day, so a writer that
    /// moved the balance without journalling shows up as a break in the chain rather than as a
    /// number nobody can account for.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal BalanceAfter { get; set; }

    /// <summary>
    /// How the movement would be described to a person — "credit note 4471", "till sale". Free text,
    /// never an identity: <see cref="DocumentKey"/> is the identity.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Reference { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
