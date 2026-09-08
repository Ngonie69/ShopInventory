namespace ShopInventory.Web.Models;

/// <summary>
/// Credit note type enum
/// </summary>
/// <remarks>
/// These numbers are the API's <c>CreditNoteType</c> and are sent to it as numbers, so the two
/// lists must agree name for name and value for value. They did not: this side read 1 as
/// <c>Refund</c>, 2 as <c>Adjustment</c> and 3 as <c>Cancellation</c>, against the API's
/// <c>PriceAdjustment</c>, <c>Discount</c> and <c>Damaged</c> — so a credit note raised here as an
/// adjustment was stored as a discount, one raised as a cancellation was stored as damaged goods,
/// and every credit note the API answered with was relabelled on the way back. Nothing failed;
/// the wrong word was simply shown and stored.
/// </remarks>
public enum CreditNoteType
{
    Return = 0,
    PriceAdjustment = 1,
    Discount = 2,
    Damaged = 3,
    Other = 4,

    /// <summary>
    /// Set by cancelling an invoice, never chosen by hand — a cancellation reverses a whole invoice
    /// and is raised from the invoice, not from the credit note form.
    /// </summary>
    Cancellation = 5
}

/// <summary>
/// Credit note status enum
/// </summary>
public enum CreditNoteStatus
{
    Draft = 0,
    Pending = 1,
    Approved = 2,
    Applied = 3,
    Cancelled = 4,
    PartiallyApplied = 5,
    Voided = 6
}

/// <summary>
/// DTO for Credit Note response
/// </summary>
public class CreditNoteDto
{
    public int Id { get; set; }
    public int? SAPDocEntry { get; set; }
    public int? SAPDocNum { get; set; }
    public string CreditNoteNumber { get; set; } = null!;
    public DateTime CreditNoteDate { get; set; }
    public string CardCode { get; set; } = null!;
    public string? CardName { get; set; }
    public CreditNoteType Type { get; set; }
    public string TypeName => Type.ToString();
    public CreditNoteStatus Status { get; set; }
    public string StatusName => Status.ToString();
    public int? OriginalInvoiceId { get; set; }
    public int? OriginalInvoiceDocEntry { get; set; }
    public int? OriginalInvoiceSAPDocEntry { get; set; }
    public int? OriginalInvoiceSAPDocNum { get; set; }
    public string? Reason { get; set; }
    public string? Comments { get; set; }
    public string? Currency { get; set; }
    public decimal ExchangeRate { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal DocTotal { get; set; }
    public decimal AppliedAmount { get; set; }
    public decimal Balance { get; set; }
    public bool RestockItems { get; set; }
    public string? RestockWarehouseCode { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovedByUserName { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsSynced { get; set; }
    public bool? IsFiscalized { get; set; }
    public string FiscalizationStatus { get; set; } = "Unknown";
    public int? FiscalReceiptGlobalNo { get; set; }
    public DateTime? FiscalizedAtUtc { get; set; }
    public List<CreditNoteLineDto> Lines { get; set; } = new();
}

/// <summary>
/// DTO for Credit Note Line
/// </summary>
public class CreditNoteLineDto
{
    public int Id { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = null!;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public string? TaxCode { get; set; }
    public decimal LineTotal { get; set; }
    public string? WarehouseCode { get; set; }
    public string? ReturnReason { get; set; }
    public string? BatchNumber { get; set; }
    public bool IsRestocked { get; set; }
}

/// <summary>
/// Request to create a credit note
/// </summary>
public class CreateCreditNoteRequest
{
    public string CardCode { get; set; } = null!;
    public string? CardName { get; set; }
    public CreditNoteType Type { get; set; } = CreditNoteType.Return;
    public int? OriginalInvoiceId { get; set; }
    public string Reason { get; set; } = null!;
    public string? Comments { get; set; }
    public string? Currency { get; set; } = "USD";
    public bool RestockItems { get; set; } = true;
    public string? RestockWarehouseCode { get; set; }
    public string? ClientRequestId { get; set; }
    public List<CreateCreditNoteLineRequest> Lines { get; set; } = new();
}

/// <summary>
/// Request to create a credit note line
/// </summary>
public class CreateCreditNoteLineRequest
{
    public string ItemCode { get; set; } = null!;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public string? WarehouseCode { get; set; }
    public string? ReturnReason { get; set; }
    public string? BatchNumber { get; set; }
    public int? OriginalInvoiceLineId { get; set; }

    /// <summary>
    /// Batch numbers for batch-managed items (required for returns)
    /// </summary>
    public List<CreditNoteBatchRequest>? BatchNumbers { get; set; }
}

/// <summary>
/// Batch number details for credit note line
/// </summary>
public class CreditNoteBatchRequest
{
    public string? BatchNumber { get; set; }
    public decimal Quantity { get; set; }
}

/// <summary>
/// Credit note list response
/// </summary>
public class CreditNoteListResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<CreditNoteDto> CreditNotes { get; set; } = new();
}

/// <summary>
/// Response for credit notes associated with a specific invoice
/// </summary>
public class CreditNotesByInvoiceResponse
{
    public int InvoiceId { get; set; }
    public bool HasExistingCreditNotes { get; set; }
    public decimal TotalCreditedAmount { get; set; }
    public List<CreditNoteDto> CreditNotes { get; set; } = new();
}
/// <summary>
/// Result of credit note creation with error information
/// </summary>
public class CreateCreditNoteResult
{
    public bool Success { get; set; }
    public CreditNoteDto? CreditNote { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// One reason SAP allows on a credit note line.
/// </summary>
/// <remarks>
/// Read from <c>GET /api/CreditNote/reasons</c> rather than listed here. SAP administers the list
/// and rejects a value it does not define, and the list is not the same in every company database,
/// so a copy on this side would be wrong the first time somebody edited it in SAP.
/// </remarks>
public class CreditNoteReasonOption
{
    /// <summary>What is stored on the line. This is what goes back to the API.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The wording a person picking a reason reads.</summary>
    public string Description { get; set; } = string.Empty;
}

public class CreditNoteReasonsResponse
{
    public List<CreditNoteReasonOption> Reasons { get; set; } = new();
}

/// <summary>
/// What the API answers when an invoice has been cancelled.
/// </summary>
/// <remarks>
/// Nullability mirrors the API's <c>CancelInvoiceResult</c> exactly. The two SAP document numbers
/// are nullable there because a credit note exists locally before SAP numbers it.
/// </remarks>
public class CancelInvoiceResult
{
    public int InvoiceDocEntry { get; set; }
    public int InvoiceDocNum { get; set; }
    public int CreditNoteId { get; set; }
    public string CreditNoteNumber { get; set; } = string.Empty;
    public int? CreditNoteDocEntry { get; set; }
    public int? CreditNoteDocNum { get; set; }
    public decimal CreditedAmount { get; set; }
    public string? Currency { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>The tills that were pushed the cancellation. Empty means none were reachable.</summary>
    public List<string> NotifiedWarehouses { get; set; } = new();
}

/// <summary>The outcome of asking the API to cancel an invoice.</summary>
public class CancelInvoiceOutcome
{
    public bool Success { get; set; }
    public CancelInvoiceResult? Result { get; set; }
    public string? ErrorMessage { get; set; }
}
