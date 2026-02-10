namespace ShopInventory.Web.Models;

/// <summary>
/// Credit note type enum
/// </summary>
public enum CreditNoteType
{
    Return = 0,
    Refund = 1,
    Adjustment = 2,
    Cancellation = 3
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
    PartiallyApplied = 4,
    Cancelled = 5,
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