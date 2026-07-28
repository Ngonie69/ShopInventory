using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Read-only local projection of an SAP A/R credit memo header. This is deliberately separate
/// from CreditNoteEntity, which represents the application's create/approve/fiscalize workflow.
/// </summary>
[Table("SapCreditNoteSnapshots")]
public sealed class SapCreditNoteSnapshotEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int SapDocEntry { get; set; }

    public int SapDocNum { get; set; }

    public DateTime DocDate { get; set; }

    [MaxLength(50)]
    public string? CardCode { get; set; }

    [MaxLength(200)]
    public string? CardName { get; set; }

    [MaxLength(10)]
    public string? DocCurrency { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DocTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal VatSum { get; set; }

    [MaxLength(30)]
    public string? DocumentStatus { get; set; }

    public bool IsCancelled { get; set; }

    public DateTime? SapUpdateDate { get; set; }

    public DateTime LastSeenInSapAtUtc { get; set; }

    public DateTime SyncedAtUtc { get; set; }

    public ICollection<SapCreditNoteLineSnapshotEntity> Lines { get; set; } =
        new List<SapCreditNoteLineSnapshotEntity>();
}

/// <summary>
/// Line-level SAP projection. BaseEntry/BaseLine/BaseType are the authoritative relationship to
/// the original invoice; a credit note can contain lines based on more than one invoice.
/// </summary>
[Table("SapCreditNoteLineSnapshots")]
public sealed class SapCreditNoteLineSnapshotEntity
{
    public int CreditNoteDocEntry { get; set; }

    public int LineNum { get; set; }

    [MaxLength(100)]
    public string? ItemCode { get; set; }

    public int? BaseEntry { get; set; }

    public int? BaseLine { get; set; }

    public int? BaseType { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal VatSum { get; set; }

    [MaxLength(500)]
    public string? CreditReason { get; set; }

    public SapCreditNoteSnapshotEntity CreditNote { get; set; } = null!;
}
