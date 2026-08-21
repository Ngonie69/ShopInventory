using System.Text.Json.Serialization;
using ShopInventory.Common.Sales;

namespace ShopInventory.DTOs;

/// <summary>
/// A batch of sales a van handset captured and ZIMRA-stamped offline, uploaded whenever it regains
/// signal. Nothing here reaches SAP on arrival — the batch is held and posted at end of day.
///
/// Sent as a batch because a van typically reconnects with a backlog, and one round trip for the day's
/// trading is the difference between draining the queue at the depot gate and not draining it at all.
/// </summary>
public sealed class VanSalesOfflineSaleBatchRequest
{
    [JsonPropertyName("sales")]
    public List<VanSalesOfflineSaleRequest> Sales { get; set; } = [];
}

/// <summary>
/// One completed van sale: the receipt the customer already holds, plus what SAP will need to invoice it
/// tonight. The fiscal fields are reported, not requested — the receipt was stamped hours ago and this
/// API must never re-fiscalise it.
/// </summary>
public sealed class VanSalesOfflineSaleRequest : IVanSalesSignedReceipt
{
    /// <summary>
    /// The handset's own reference, e.g. <c>VAN006-INV-20260810-D261C8</c>. It is the idempotency key
    /// end to end: unique on the local receipts table, the unique
    /// <c>DesktopSaleEntity.ExternalReferenceId</c> here, and the <c>U_Van_saleorder</c> that stops SAP
    /// accepting the same sale twice when the mop-up re-runs.
    /// </summary>
    [JsonPropertyName("van_order")]
    public string VanOrder { get; set; } = string.Empty;

    /// <summary>SAP business partner the invoice posts against.</summary>
    [JsonPropertyName("customer_code")]
    public string? CustomerCode { get; set; }

    [JsonPropertyName("customer_name")]
    public string? CustomerName { get; set; }

    /// <summary>
    /// When the sale was made on the handset, in the van's local time. Not the upload time — a sale made
    /// on Tuesday and uploaded on Wednesday belongs to Tuesday's trading and Tuesday's fiscal day.
    /// </summary>
    [JsonPropertyName("sold_at")]
    public DateTime SoldAt { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("total")]
    public decimal Total { get; set; }

    [JsonPropertyName("vat_amount")]
    public decimal VatAmount { get; set; }

    [JsonPropertyName("amount_paid")]
    public decimal AmountPaid { get; set; }

    [JsonPropertyName("payment_method")]
    public string? PaymentMethod { get; set; }

    [JsonPropertyName("payment_reference")]
    public string? PaymentReference { get; set; }

    [JsonPropertyName("items")]
    public List<VanSalesOfflineSaleItemRequest> Items { get; set; } = [];

    // --- Already fiscalised on the handset; reported here for the record and for reconciliation ---

    [JsonPropertyName("fiscal_device_id")]
    public string? FiscalDeviceId { get; set; }

    [JsonPropertyName("fiscal_day_no")]
    public int? FiscalDayNo { get; set; }

    [JsonPropertyName("receipt_global_no")]
    public int? ReceiptGlobalNo { get; set; }

    [JsonPropertyName("receipt_counter")]
    public int? ReceiptCounter { get; set; }

    [JsonPropertyName("verification_code")]
    public string? VerificationCode { get; set; }

    [JsonPropertyName("qr_code")]
    public string? QrCode { get; set; }

    // --- The receipt exactly as it was signed, so ZIMRA can still be given it ---
    //
    // The handset signs a canonical payload built from the device id, receipt type, currency, global
    // number, receipt date, total, tax block and the previous receipt's hash. The fiscalisation platform
    // re-derives that payload from what we forward and refuses anything that does not hash to the
    // signature below, so every field here is reported verbatim: never rounded, re-derived, or defaulted.

    /// <summary>
    /// The instant the receipt was signed at, in the van's local wall clock at second precision.
    ///
    /// Distinct from <see cref="SoldAt"/> even though today's handsets derive one from the other: SoldAt
    /// decides the trading day a sale posts against and may be normalised, whereas this is part of the
    /// signed payload and a one-second difference invalidates the signature.
    /// </summary>
    [JsonPropertyName("receipt_date")]
    public DateTime? ReceiptDate { get; set; }

    /// <summary>
    /// When the handset's fiscal day was opened, local wall clock. The platform never opened this day
    /// itself, so it has no other way to learn it, and the offline file's header declares it.
    /// </summary>
    [JsonPropertyName("fiscal_day_opened_at")]
    public DateTime? FiscalDayOpenedAt { get; set; }

    /// <summary>
    /// The hash this receipt was chained onto, or null for the first receipt of a fiscal day. Sent
    /// explicitly rather than inferred from the receipt before it, so a divergence is reported as the
    /// chain break it is instead of surfacing as an unexplained signature failure.
    /// </summary>
    [JsonPropertyName("previous_receipt_hash")]
    public string? PreviousReceiptHash { get; set; }

    /// <summary>Base64 SHA-256 of the canonical payload the handset signed.</summary>
    [JsonPropertyName("device_signature_hash")]
    public string? DeviceSignatureHash { get; set; }

    /// <summary>Base64 RSA-PKCS1-SHA256 signature over that same payload.</summary>
    [JsonPropertyName("device_signature_value")]
    public string? DeviceSignatureValue { get; set; }

    /// <summary>Whether this sale carries everything the platform needs to accept the receipt.</summary>
    /// <remarks>
    /// Shared with the online path — see <see cref="VanSalesSignedReceipt.HasSignedReceipt"/>. Both van
    /// endpoints must answer this identically or the same cart would be judged differently depending on
    /// whether the van happened to have signal.
    /// </remarks>
    public bool HasSignedReceipt() => VanSalesSignedReceipt.HasSignedReceipt(this);

    /// <summary>
    /// Whether the handset took a number off its device's chain for this sale.
    /// See <see cref="VanSalesSignedReceipt.ClaimsReceiptSequence"/> for what turns on the answer.
    /// </summary>
    public bool ClaimsReceiptSequence() => VanSalesSignedReceipt.ClaimsReceiptSequence(this);
}

/// <summary>
/// One line, serving two masters: SAP invoices it tonight, and the fiscalisation platform rebuilds the
/// signed receipt from it. The fiscal fields are therefore reported as the handset signed them —
/// <see cref="Price"/> in particular is the tax-inclusive unit price the receipt was signed over, not a
/// list price, because the platform recomputes the line total and the tax block from it.
/// </summary>
public sealed class VanSalesOfflineSaleItemRequest
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>Also the receipt line's name, so it is part of what the platform rebuilds.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("tax_code")]
    public string? TaxCode { get; set; }

    /// <summary>The FDMS tax id the line was signed under, from the handset's fiscal lease.</summary>
    [JsonPropertyName("tax_id")]
    public int? TaxId { get; set; }

    /// <summary>
    /// The rate in force when the receipt was signed. Null and zero are different — null is untaxed and
    /// contributes nothing to the signed payload, zero is a zero rate and contributes "0.00".
    /// </summary>
    [JsonPropertyName("tax_percent")]
    public decimal? TaxPercent { get; set; }

    [JsonPropertyName("hs_code")]
    public string? HsCode { get; set; }

    /// <summary>
    /// The unit the line was sold and priced in — the unit <see cref="Quantity"/> counts and
    /// <see cref="Price"/> is per.
    /// </summary>
    /// <remarks>
    /// Reported, never acted on. It exists because <c>VanSaleLineFact</c> cannot be totalled without
    /// it: a report that sums quantity across items mixes eaches with kilograms and produces a figure
    /// that looks like a number and means nothing. Null from a handset that predates this field, which
    /// reads as "not recorded" rather than as any particular unit.
    /// </remarks>
    [JsonPropertyName("uom_code")]
    public string? UoMCode { get; set; }

    /// <summary>
    /// The discount given on this line, as a percentage of the undiscounted price.
    /// </summary>
    /// <remarks>
    /// <b>Reported, never applied.</b> <see cref="Price"/> is the tax-inclusive unit price the receipt
    /// was actually signed over and is already net of this. Recomputing a line total from the two would
    /// contradict a figure ZIMRA holds a signature for, and the platform refuses a receipt whose
    /// recomputed payload does not hash to what the device signed.
    /// </remarks>
    [JsonPropertyName("discount_percent")]
    public decimal? DiscountPercent { get; set; }
}

/// <summary>
/// Per-sale outcome. Reported individually rather than failing the batch: a van's backlog is a day's
/// takings, and one malformed row must not strand the rest on the handset.
/// </summary>
public sealed class VanSalesOfflineSaleBatchResponse
{
    [JsonPropertyName("accepted")]
    public int Accepted { get; set; }

    [JsonPropertyName("duplicates")]
    public int Duplicates { get; set; }

    [JsonPropertyName("rejected")]
    public int Rejected { get; set; }

    [JsonPropertyName("results")]
    public List<VanSalesOfflineSaleResultDto> Results { get; set; } = [];
}

public sealed class VanSalesOfflineSaleResultDto
{
    [JsonPropertyName("van_order")]
    public string VanOrder { get; set; } = string.Empty;

    /// <summary>
    /// <c>accepted</c>, <c>duplicate</c> or <c>rejected</c>. A duplicate is a success from the handset's
    /// point of view — the sale is safely held — and it must delete its queued copy on either.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Set once the sale has posted, so the handset can settle its local stock ledger.</summary>
    [JsonPropertyName("sap_doc_num")]
    public int? SapDocNum { get; set; }
}
