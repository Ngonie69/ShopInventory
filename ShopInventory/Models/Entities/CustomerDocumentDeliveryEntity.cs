using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One document sent, or to be sent, to one WhatsApp number — the outbox and the record of what went.
/// </summary>
/// <remarks>
/// <para>
/// Every send goes through a row here and through one clustered job, whoever asked for it. That is
/// what gives every send one throttle, one guard against sending twice, and one place to read what a
/// customer was sent. A web request only ever writes a row; it never calls the gateway itself.
/// </para>
/// <para>
/// The document is a SAP invoice (<see cref="SapDocEntry"/>) or a till sale's receipt
/// (<see cref="DesktopSaleId"/>), never both; a check constraint holds that. The customer, totals and
/// recipient are copied onto the row when it is queued, so the history reads the same after the
/// customer's details change.
/// </para>
/// <para>
/// The PDF itself is not kept. <see cref="FileSha256"/>, <see cref="FileBytes"/> and the fiscal codes
/// are the record of exactly what was sent; the PDF can be rendered again from SAP.
/// </para>
/// </remarks>
public class CustomerDocumentDeliveryEntity
{
    [Key]
    public long Id { get; set; }

    public CustomerDocumentType DocumentType { get; set; }

    public int? SapDocEntry { get; set; }

    public int? SapDocNum { get; set; }

    public int? DesktopSaleId { get; set; }

    /// <summary>The number the customer knows the document by: the SAP DocNum, or a till's INV number.</summary>
    [Required]
    [MaxLength(50)]
    public string DocumentNumber { get; set; } = null!;

    /// <summary>The sale reference SAP keeps on the invoice (<c>U_Van_saleorder</c>), when there is one.</summary>
    [MaxLength(100)]
    public string? SaleReference { get; set; }

    [Column(TypeName = "date")]
    public DateTime? DocumentDate { get; set; }

    /// <summary>SAP's <c>DocTotal</c>, in the company's local currency.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? DocumentTotal { get; set; }

    /// <summary>
    /// SAP's <c>DocTotalFc</c>, set only for a document in a foreign currency — which is then the total
    /// the customer and the fiscal receipt both state.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? DocumentTotalFc { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    [MaxLength(50)]
    public string? CardCode { get; set; }

    [MaxLength(200)]
    public string? CardName { get; set; }

    public int? RouteCustomerId { get; set; }

    [MaxLength(50)]
    public string? RouteCustomerCode { get; set; }

    [MaxLength(200)]
    public string? RouteCustomerName { get; set; }

    /// <summary>The register entry this was sent under; null for a one-off number.</summary>
    public int? ContactId { get; set; }

    [Required]
    [MaxLength(20)]
    public string RecipientE164 { get; set; } = null!;

    [MaxLength(100)]
    public string? RecipientName { get; set; }

    /// <summary>
    /// When this row's own number was checked against WhatsApp. Set only for a number typed for one
    /// send; a saved contact keeps its check on the contact.
    /// </summary>
    public DateTime? RecipientCheckedAtUtc { get; set; }

    public CustomerDocumentDeliveryTrigger Trigger { get; set; }

    /// <summary>
    /// Whether the person who asked for a one-off send confirmed the customer agreed to it. A saved
    /// contact carries its own consent; this is the only record of it for a number typed at the time.
    /// </summary>
    public bool ConsentAffirmed { get; set; }

    public Guid? RequestedByUserId { get; set; }

    [MaxLength(100)]
    public string? RequestedBy { get; set; }

    /// <summary>Higher goes first. A person waiting on a send is served before the automatic queue.</summary>
    public int Priority { get; set; }

    public CustomerDocumentDeliveryStatus Status { get; set; }

    /// <summary>Why the row stands where it does, in words for the person reading the history.</summary>
    [MaxLength(500)]
    public string? StatusReason { get; set; }

    /// <summary>The earliest the job looks at this row again.</summary>
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Sends tried that provably never left. A send that may have left is never counted here; it is Uncertain.</summary>
    public int DispatchAttempts { get; set; }

    /// <summary>The pass holding the row while it prepares and sends it.</summary>
    public Guid? ClaimToken { get; set; }

    public DateTime? ClaimedAtUtc { get; set; }

    /// <summary>When the gateway was called. Every cap and the gap between sends count from this.</summary>
    public DateTime? SendIssuedAtUtc { get; set; }

    public DateTime? SentAtUtc { get; set; }

    /// <summary>WhatsApp's id for the message, when it gave one.</summary>
    [MaxLength(200)]
    public string? MessageId { get; set; }

    public long? GatewayTimestamp { get; set; }

    /// <summary>The OpenWA session it was sent from.</summary>
    [MaxLength(64)]
    public string? SessionId { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    [MaxLength(200)]
    public string? FileName { get; set; }

    [MaxLength(64)]
    public string? FileSha256 { get; set; }

    public int? FileBytes { get; set; }

    [MaxLength(1024)]
    public string? Caption { get; set; }

    [MaxLength(500)]
    public string? FiscalQrCode { get; set; }

    [MaxLength(200)]
    public string? FiscalVerificationCode { get; set; }

    /// <summary>Which record vouched for the fiscal receipt that was printed.</summary>
    [MaxLength(30)]
    public string? FiscalEvidenceSource { get; set; }

    /// <summary>The earlier delivery this one re-sends, when it is a resend.</summary>
    public long? SupersedesDeliveryId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the row reached a state the job no longer acts on.</summary>
    public DateTime? ClosedAtUtc { get; set; }

    [MaxLength(100)]
    public string? ClosedBy { get; set; }
}
