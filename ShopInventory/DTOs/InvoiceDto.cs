using ShopInventory.Services;

namespace ShopInventory.DTOs;

/// <summary>
/// DTO for invoice response
/// </summary>
public class InvoiceDto
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string? DocDate { get; set; }
    public string? DocDueDate { get; set; }
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public string? NumAtCard { get; set; }
    public string? Comments { get; set; }
    public string? DocStatus { get; set; }
    public string? Remarks { get; set; }
    public string? VanSaleOrderNumber { get; set; }
    public bool IsVanSalesInvoice { get; set; }
    public decimal DocTotal { get; set; }
    public decimal PaidToDate { get; set; }
    public decimal VatSum { get; set; }
    public string? DocCurrency { get; set; }
    public bool? IsFiscalized { get; set; }
    public string FiscalizationStatus { get; set; } = "Unknown";

    /// <summary>
    /// Reposted after the SAP update and already fiscalised under its old number, so never fiscalised
    /// again. Set by the invoice list and single-invoice reads; the fiscalise route checks the remarks
    /// itself rather than trusting this.
    /// </summary>
    public bool IsRepostedAfterSapUpdate { get; set; }
    public string? FiscalQrCode { get; set; }
    public int? FiscalReceiptGlobalNo { get; set; }
    public string? FiscalVerificationCode { get; set; }
    public string? FiscalDeviceId { get; set; }
    public string? FiscalDay { get; set; }
    public DateTime? FiscalizedAtUtc { get; set; }

    /// <summary>
    /// How much of this invoice has been credited back, tax-inclusive like <see cref="DocTotal"/>. Null
    /// when the read did not look — only the till's customer invoice list does — never "not credited".
    /// </summary>
    public decimal? CreditedAmount { get; set; }

    /// <summary>The numbers of the credits behind <see cref="CreditedAmount"/>, when it was looked up.</summary>
    public List<string>? CreditNoteNumbers { get; set; }

    // Address & tax fields (populated from SAP invoice + business partner)
    public string? BillToAddress { get; set; }
    public string? ShipToAddress { get; set; }
    public string? CustomerVatNo { get; set; }
    public string? CustomerTinNumber { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerEmail { get; set; }

    public List<InvoiceLineDto>? Lines { get; set; }
}

/// <summary>
/// DTO for invoice line
/// </summary>
public class InvoiceLineDto
{
    public int LineNum { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>Gross unit price BEFORE the line discount — see <see cref="PriceAfterVat"/>.</summary>
    public decimal GrossPrice { get; set; }

    /// <summary>Gross unit price after the line discount. What the customer paid.</summary>
    public decimal PriceAfterVat { get; set; }

    /// <summary>Gross line total after the line discount.</summary>
    public decimal GrossTotal { get; set; }

    /// <summary>SAP VAT group code (OVTG.Code). Where the tax code actually lives; TaxCode is null.</summary>
    public string? VatGroup { get; set; }
    public decimal LineTotal { get; set; }
    public string? TaxCode { get; set; }
    public string? WarehouseCode { get; set; }
    public decimal DiscountPercent { get; set; }
    public string? UoMCode { get; set; }

    /// <summary>
    /// How many of this line have been credited back. Null when the read did not look — only the till's
    /// invoice reads do — never "nothing credited".
    /// </summary>
    public decimal? CreditedQuantity { get; set; }

    /// <summary>What was credited back on this line, tax included, when the read looked.</summary>
    public decimal? CreditedAmount { get; set; }
}

/// <summary>
/// DTO for invoice creation response
/// </summary>
public class InvoiceCreatedResponseDto
{
    public string Message { get; set; } = "Invoice created successfully";
    public InvoiceDto? Invoice { get; set; }

    /// <summary>
    /// Fiscalization result. Null if fiscalization was not attempted.
    /// </summary>
    public FiscalizationResult? Fiscalization { get; set; }
}

/// <summary>
/// DTO for paginated invoice list response
/// </summary>
public class InvoiceListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasMore { get; set; }
    public List<InvoiceDto>? Invoices { get; set; }

    /// <summary>Totals over every invoice the filters match, when asked for with <c>includeSummary</c>.</summary>
    public InvoiceListSummaryDto? Summary { get; set; }

    /// <summary>
    /// True when a fiscal filter's scan stopped at its limit, so invoices past it were not considered.
    /// </summary>
    public bool ScanLimitReached { get; set; }
}

/// <summary>The Invoices page's tiles, over every invoice the filters match.</summary>
public class InvoiceListSummaryDto
{
    public int Count { get; set; }
    public decimal Total { get; set; }
    public decimal Vat { get; set; }
    public int Customers { get; set; }

    /// <summary>How many could be fiscalised; known only when the fiscal state was looked up for every match.</summary>
    public int? FiscalisableCount { get; set; }
}

/// <summary>What <c>ISAPServiceLayerClient.SummarizeInvoicesAsync</c> adds up.</summary>
public sealed record InvoiceListTotals(int Count, decimal Total, decimal Vat, int Customers);

/// <summary>
/// DTO for invoice list by date response
/// </summary>
public class InvoiceDateResponseDto
{
    public string? Date { get; set; }
    public string? FromDate { get; set; }
    public string? ToDate { get; set; }
    public string? Customer { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasMore { get; set; }
    public List<InvoiceDto>? Invoices { get; set; }
}

/// <summary>
/// Request to create a credit note from an invoice
/// </summary>
public class CreateCreditNoteFromInvoiceRequest
{
    /// <summary>
    /// The document entry of the invoice to create credit note from
    /// </summary>
    public int InvoiceDocEntry { get; set; }

    /// <summary>
    /// Reason for the credit note
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Optional: specific lines to include (if not provided, all lines are included)
    /// </summary>
    public List<CreditNoteLineRequest>? Lines { get; set; }
}

/// <summary>
/// Credit note line request
/// </summary>
public class CreditNoteLineRequest
{
    public int LineNum { get; set; }
    public decimal Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
}
