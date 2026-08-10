using System.Text.Json.Serialization;

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
public sealed class VanSalesOfflineSaleRequest
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
}

public sealed class VanSalesOfflineSaleItemRequest
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("tax_code")]
    public string? TaxCode { get; set; }
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
