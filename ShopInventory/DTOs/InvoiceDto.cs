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
    public decimal DocTotal { get; set; }
    public decimal VatSum { get; set; }
    public string? DocCurrency { get; set; }
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
    public decimal LineTotal { get; set; }
    public string? WarehouseCode { get; set; }
    public decimal DiscountPercent { get; set; }
    public string? UoMCode { get; set; }
}

/// <summary>
/// DTO for invoice creation response
/// </summary>
public class InvoiceCreatedResponseDto
{
    public string Message { get; set; } = "Invoice created successfully";
    public InvoiceDto? Invoice { get; set; }
}

/// <summary>
/// DTO for paginated invoice list response
/// </summary>
public class InvoiceListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public List<InvoiceDto>? Invoices { get; set; }
}

/// <summary>
/// DTO for invoice list by date response
/// </summary>
public class InvoiceDateResponseDto
{
    public string? Date { get; set; }
    public string? FromDate { get; set; }
    public string? ToDate { get; set; }
    public int Count { get; set; }
    public List<InvoiceDto>? Invoices { get; set; }
}
