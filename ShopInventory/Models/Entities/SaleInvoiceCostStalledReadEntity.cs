using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One read of invoice cost that SAP took and never answered: a warehouse and a run of invoice numbers
/// that <see cref="Services.SaleInvoiceCostSyncService"/> does not ask SAP for again.
/// </summary>
/// <remarks>
/// <para>
/// A stored-query read SAP stalls on leaves a Service Layer process working on it for good, at a full
/// core and up to 8 GB (<see cref="Services.SapStuckReadState"/>). The invoices of such a read stay
/// uncosted, so the next run planned the same read and sent it again. Overnight on 2026-11-22/23 that
/// was every 15 minutes: <c>CORMACH2</c>'s invoices 2433442-2433724 and <c>KEFGRC</c>'s
/// 2433446-2433670, each send another stuck process, 13 of them holding 76.8 GB by 04:45.
/// </para>
/// <para>
/// A posted invoice's lines do not change, so the same range under the same statement is the same
/// answer, and the same stall. The record is therefore kept for as long as the statement is the one
/// that stalled (<see cref="StatementId"/>): a release that changes the statement asks once more, and
/// nothing else does. In the database rather than in memory, because the job runs on whichever node
/// the scheduler picks and a recycle would forget it. Deleting a row is how a person has the range
/// asked for again.
/// </para>
/// </remarks>
[Index(nameof(StatementId), nameof(WarehouseCode))]
public class SaleInvoiceCostStalledReadEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>The warehouse whose lines were asked for.</summary>
    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>The first invoice (<c>DocEntry</c>) of the range SAP was asked for.</summary>
    public int DocEntryStart { get; set; }

    /// <summary>The last invoice (<c>DocEntry</c>) of the range SAP was asked for.</summary>
    public int DocEntryEnd { get; set; }

    /// <summary>The statement that stalled: the fingerprint of its SQL text.</summary>
    [Required]
    [MaxLength(32)]
    public string StatementId { get; set; } = string.Empty;

    /// <summary>When SAP was given up on, UTC.</summary>
    public DateTime StalledAt { get; set; }
}
