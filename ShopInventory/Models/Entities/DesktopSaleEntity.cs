using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

public enum DesktopSaleFiscalizationStatus
{
    Pending,
    Success,
    Failed,
    Skipped
}

public enum DesktopSaleConsolidationStatus
{
    Pending,
    Consolidated,
    Failed,
    Excluded
}

/// <summary>
/// Whether the ZIMRA receipt a handset signed for itself has been handed to the fiscalisation platform.
///
/// Only a sale a van handset stamped for itself has one — by either route, offline batch or online direct
/// invoice, because a handset owns one device and stamps off its one chain whatever the network was doing.
/// Every other sale on this table was fiscalised by the platform in the first place, so there is nothing to
/// hand over and the status stays <see cref="NotApplicable"/>.
///
/// This is deliberately separate from <see cref="DesktopSaleFiscalizationStatus"/>. That answers "does a
/// fiscal receipt exist for this sale", and for an offline van sale it is <c>Success</c> the moment the
/// upload arrives — the customer is holding the printed receipt. This answers the different and equally
/// necessary question of whether ZIMRA will ever see it.
/// </summary>
public enum DesktopSaleReceiptIngestStatus
{
    /// <summary>Nothing to submit: the sale was fiscalised online, or is not a van sale at all.</summary>
    NotApplicable = 0,

    /// <summary>Signed on the handset and waiting to be handed to the platform.</summary>
    Pending = 1,

    /// <summary>The platform has archived it. It reaches ZIMRA when the fiscal day's file is uploaded.</summary>
    Ingested = 2,

    /// <summary>Refused for a reason that may not repeat — a network failure, or a transient platform error.</summary>
    Failed = 3,

    /// <summary>
    /// The platform says this receipt does not continue its device's chain. Retrying cannot fix it and
    /// must not be attempted: the device stops here until a person reconciles it.
    /// </summary>
    ChainBroken = 4,

    /// <summary>
    /// The upload carried no usable signature, so this receipt can never be submitted. The sale is still
    /// held and posted — the money is real — but the fiscal side needs a person.
    /// </summary>
    Unsignable = 5,

    /// <summary>
    /// A van sale that was never stamped: the handset produced no receipt at all, because it is on a
    /// build older than the signing release.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from both neighbours, and the distinction is what keeps a device trading.
    /// <see cref="NotApplicable"/> would say there was nothing to submit, which is untrue — this sale
    /// should have been stamped and was not. <see cref="Unsignable"/> would say a receipt exists but
    /// cannot be archived, which is worse than untrue: the drain treats that as a hole in the chain and
    /// stops the device, and an unstamped sale is not in the chain at all. It consumed no receipt
    /// number, so nothing is waiting behind it.
    ///
    /// The drain therefore skips this status. It is counted on the fiscalisation console at
    /// <c>/fiscalisation</c> — per device, and under the work queue's "Never stamped" filter — and listed
    /// in the Exception Center under <c>fiscal-receipt-ingest</c>, distinguished there from a chain hole
    /// because the remedy is a handset update rather than a reconciliation. Once
    /// <see cref="Configuration.FiscalisationSettings.RequireStampedVanSales"/> is on, no new row can
    /// reach it.
    /// </remarks>
    Unstamped = 6
}

/// <summary>
/// A local invoice created by the desktop app during the day.
/// Fiscalised immediately; posted to SAP at end of day as part of a consolidated invoice.
/// </summary>
[Index(nameof(ExternalReferenceId), IsUnique = true)]
[Index(nameof(CardCode))]
[Index(nameof(ConsolidationStatus))]
[Index(nameof(DocDate))]
// Read on every page of invoices, to recognise the ones whose sale was already fiscalised.
[Index(nameof(SapDocNum))]
[Index(nameof(WarehouseCode))]
// The drain walks one device's receipts in signing order, so it filters on the status, groups by device
// and sorts on the global number. All three together, because a van's day is a few dozen rows in a table
// of every sale ever made.
[Index(nameof(ReceiptIngestStatus), nameof(FiscalDeviceId), nameof(ReceiptGlobalNo))]
// The pre-post duplicate check asks "has this mobile invoice number already been posted", once per sale.
[Index(nameof(SapComexReference))]
// Reporting reads a route customer's sales in date order, and the fallback path reads them by code for
// rows whose customer record has since been deleted.
[Index(nameof(RouteCustomerId), nameof(DocDate))]
[Index(nameof(RouteCustomerCode))]
public class DesktopSaleEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Unique reference from the desktop app — idempotency key.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ExternalReferenceId { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? SourceSystem { get; set; }

    [Required]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CardName { get; set; }

    // --- Which shop on the route actually bought this ---
    //
    // A van sale posts against the van's own business partner, because that is the account SAP bills, so
    // CardCode above says which van sold rather than who bought. The shop is recorded here and nowhere
    // else: without it, a route's takings are one undifferentiated number and no customer has a history.
    //
    // Null on every sale that is not a route-customer van sale — the desktop route, and vans whose
    // customers are real SAP business partners, both leave it unset.

    public int? RouteCustomerId { get; set; }

    [ForeignKey(nameof(RouteCustomerId))]
    public RouteCustomerEntity? RouteCustomer { get; set; }

    /// <summary>
    /// The route customer's code and name as they stood when the sale was made.
    ///
    /// Snapshots, not a convenience copy of the joined row. Route customers are hard-deleted, which nulls
    /// the id above, and they are freely renamed; a sale that happened must keep the name it happened to,
    /// or last year's report changes every time someone tidies the customer list.
    /// </summary>
    [MaxLength(50)]
    public string? RouteCustomerCode { get; set; }

    [MaxLength(200)]
    public string? RouteCustomerName { get; set; }

    [Column(TypeName = "date")]
    public DateTime DocDate { get; set; }

    public int? SalesPersonCode { get; set; }

    [MaxLength(100)]
    public string? NumAtCard { get; set; }

    [MaxLength(500)]
    public string? Comments { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal VatAmount { get; set; }

    [MaxLength(10)]
    public string Currency { get; set; } = "ZWG";

    // --- Fiscalization ---

    public DesktopSaleFiscalizationStatus FiscalizationStatus { get; set; } = DesktopSaleFiscalizationStatus.Pending;

    [MaxLength(100)]
    public string? FiscalReceiptNumber { get; set; }

    /// <summary>
    /// The device's serial as the platform reported it. Display only.
    /// </summary>
    /// <remarks>
    /// Not the device id, despite the name, and not reliably a serial either: the online path writes the
    /// serial the platform returned, while an offline van upload writes the numeric device id as a
    /// string. Both meanings are already in the table, which is why
    /// <see cref="FiscalDeviceId"/> exists — read that when a device is being identified.
    /// </remarks>
    [MaxLength(100)]
    public string? FiscalDeviceNumber { get; set; }

    /// <summary>
    /// The ZIMRA device this sale's receipt belongs to, when it belongs to one.
    /// </summary>
    /// <remarks>
    /// The chain is per device, so every question the ingest asks — what is outstanding, in what order,
    /// which device is stopped — is a question about this column. It was previously parsed out of
    /// <see cref="FiscalDeviceNumber"/> on each pass, which worked only because the two writers of that
    /// column never overlapped in one query.
    /// </remarks>
    public int? FiscalDeviceId { get; set; }

    [MaxLength(500)]
    public string? FiscalQRCode { get; set; }

    [MaxLength(200)]
    public string? FiscalVerificationCode { get; set; }

    [MaxLength(500)]
    public string? FiscalVerificationLink { get; set; }

    [MaxLength(50)]
    public string? FiscalDayNo { get; set; }

    [MaxLength(2000)]
    public string? FiscalError { get; set; }

    /// <summary>
    /// How many times fiscalisation has been attempted. A shop till fiscalises inline and so is
    /// normally 1; a vending sale is fiscalised by a background sweep, which needs somewhere to give
    /// up rather than re-offering the same broken sale to the platform forever.
    /// </summary>
    public int FiscalizationAttempts { get; set; }

    /// <summary>
    /// The platform could not say whether the receipt was signed, so this sale must not be retried.
    /// </summary>
    /// <remarks>
    /// The one failure that is worse to retry than to leave alone. A receipt may already exist at FDMS
    /// under this sale's reference, and a second submission cannot be withdrawn — so the sweep skips
    /// these and a human looks the receipt up instead.
    /// </remarks>
    public bool FiscalizationRequiresReconciliation { get; set; }

    /// <summary>
    /// The ZIMRA receipt's global number, and the counter within its fiscal day.
    ///
    /// Only van sales carry these, and they are what make a posted SAP invoice traceable back to the
    /// receipt the customer holds. Reconciliation between SAP and FDMS has nothing else to join on: the
    /// receipt records the SAP DocNum as its InvoiceNo, but that is assigned hours later, at posting.
    /// </summary>
    public int? ReceiptGlobalNo { get; set; }

    public int? ReceiptCounter { get; set; }

    // --- The receipt as the handset signed it, held until the platform has taken it ---
    //
    // A receipt signed offline reaches ZIMRA only if it is handed to the fiscalisation platform, which
    // archives it and includes it in the fiscal day's offline file. The platform re-derives the signed
    // payload and refuses anything that does not hash to the signature below, so these are stored exactly
    // as they arrived and are never recomputed here.

    /// <summary>
    /// The instant the receipt was signed at, local wall clock at second precision — part of the signed
    /// payload, and not to be confused with <see cref="DocDate"/>, which is only the trading day.
    /// </summary>
    /// <remarks>
    /// Stored without a time zone, unlike every other timestamp on this table. Those are UTC instants;
    /// this is the taxpayer's wall clock, carrying no offset by design — the fiscal day it belongs to is
    /// stated in the same clock, and converting it moves receipts across the day boundary. Npgsql would
    /// also refuse to write it: a <c>timestamptz</c> column rejects a <c>DateTime</c> that is not UTC.
    /// </remarks>
    [Column(TypeName = "timestamp without time zone")]
    public DateTime? ReceiptDate { get; set; }

    /// <summary>When the handset opened the fiscal day it signed this receipt into, in the same clock.</summary>
    [Column(TypeName = "timestamp without time zone")]
    public DateTime? FiscalDayOpenedAt { get; set; }

    /// <summary>The hash this receipt was chained onto; null for the first receipt of a fiscal day.</summary>
    [MaxLength(500)]
    public string? PreviousReceiptHash { get; set; }

    /// <summary>Base64 SHA-256 of the canonical payload the handset signed.</summary>
    [MaxLength(500)]
    public string? DeviceSignatureHash { get; set; }

    /// <summary>Base64 RSA-PKCS1-SHA256 signature over that payload. 2048-bit keys give 344 characters.</summary>
    [MaxLength(1000)]
    public string? DeviceSignatureValue { get; set; }

    public DesktopSaleReceiptIngestStatus ReceiptIngestStatus { get; set; } =
        DesktopSaleReceiptIngestStatus.NotApplicable;

    public int ReceiptIngestAttempts { get; set; }

    [MaxLength(2000)]
    public string? ReceiptIngestError { get; set; }

    public DateTime? ReceiptIngestedAt { get; set; }

    /// <summary>The platform's own id for the archived receipt, for tracing one back to the other.</summary>
    public long? PlatformReceiptId { get; set; }

    // --- Consolidation / posting ---

    /// <summary>
    /// Where the sale is in its journey to SAP. <see cref="DesktopSaleConsolidationStatus.Consolidated"/>
    /// means "SAP has it" for both routes: the desktop app's sales reach SAP inside a consolidated
    /// invoice, while van sales post one-to-one (see <c>VanSalesEndOfDayPostingService</c>) so that each
    /// SAP invoice still maps to exactly one ZIMRA receipt.
    /// </summary>
    public DesktopSaleConsolidationStatus ConsolidationStatus { get; set; } = DesktopSaleConsolidationStatus.Pending;

    public int? ConsolidationId { get; set; }

    [ForeignKey(nameof(ConsolidationId))]
    public SaleConsolidationEntity? Consolidation { get; set; }

    /// <summary>
    /// The SAP invoice this sale posted as, for the one-to-one route. Null for a consolidated sale,
    /// which is traced through <see cref="Consolidation"/> instead.
    /// </summary>
    public int? SapDocEntry { get; set; }

    /// <remarks>
    /// Read back by <c>PerSaleInvoiceRegistry</c> to recognise a SAP invoice as one whose sale was
    /// already fiscalised, which is what keeps the Fiscalise button off it. That lookup runs for every
    /// page of invoices, hence the index.
    /// </remarks>
    public int? SapDocNum { get; set; }

    /// <summary>
    /// The mobile invoice number written to SAP's invoice-number UDF, when one is configured.
    /// </summary>
    /// <remarks>
    /// Held locally as well as in SAP so the pre-post duplicate check can be answered without a Service
    /// Layer round trip, and so it survives a SAP outage — the check matters most exactly when SAP is
    /// unreachable, which is when a post is most likely to be retried.
    ///
    /// Null while <see cref="Configuration.FiscalisationUdfSettings.InvoiceNumberField"/> is unset.
    /// </remarks>
    [MaxLength(100)]
    public string? SapComexReference { get; set; }

    public DateTime? PostedAt { get; set; }

    /// <summary>
    /// Posting attempts so far. The 18:00 run and the 19:30 mop-up both increment this, so a sale that
    /// fails every night is visible rather than silently retried forever.
    /// </summary>
    public int PostingAttempts { get; set; }

    [MaxLength(2000)]
    public string? LastPostingError { get; set; }

    // --- The incoming payment that settles the invoice ---
    //
    // Held separately from the invoice fields above because the two steps fail independently: the
    // invoice can post and the payment fail, leaving a real open A/R document that must not be
    // re-invoiced on the next pass. SAP has no business key for a payment — ClientRequestId is not
    // forwarded and there is no get-by-reference — so unlike the invoice a payment cannot be probed
    // for afterwards. These fields are the only record that one was attempted.

    public int? PaymentSapDocEntry { get; set; }

    public int? PaymentSapDocNum { get; set; }

    public DateTime? PaymentPostedAt { get; set; }

    /// <summary>
    /// Where the settlement got to: null (not attempted), <c>Posted</c>, <c>PostedUnconfirmed</c>
    /// (SAP already showed the invoice settled, so nothing was sent), <c>Failed</c>, or
    /// <c>Unmapped</c> (the tender does not map to a SAP payment means — see
    /// <see cref="ShopInventory.Common.Sales.TenderTypes"/>).
    /// </summary>
    [MaxLength(50)]
    public string? PaymentStatus { get; set; }

    [MaxLength(2000)]
    public string? LastPaymentError { get; set; }

    // --- Warehouse / Payment ---

    [Required]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// The van's cost centre. Mandatory for a van sale — <c>CreateVanSalesDirectInvoiceHandler</c>
    /// refuses to build an invoice without one — and unused by the desktop route.
    /// </summary>
    [MaxLength(50)]
    public string? CostCentreCode { get; set; }

    [MaxLength(50)]
    public string? PaymentMethod { get; set; }

    [MaxLength(100)]
    public string? PaymentReference { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountPaid { get; set; }

    // --- Audit ---

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<DesktopSaleLineEntity> Lines { get; set; } = new();
}
