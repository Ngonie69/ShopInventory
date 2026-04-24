using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

public class PurchaseInvoiceDto
{
    [JsonPropertyName("docEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("docNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("cardCode")]
    public string CardCode { get; set; } = string.Empty;

    [JsonPropertyName("cardName")]
    public string CardName { get; set; } = string.Empty;

    [JsonPropertyName("docDate")]
    public DateTime DocDate { get; set; }

    [JsonPropertyName("docDueDate")]
    public DateTime? DocDueDate { get; set; }

    [JsonPropertyName("taxDate")]
    public DateTime? TaxDate { get; set; }

    [JsonPropertyName("docTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("docCurrency")]
    public string DocCurrency { get; set; } = string.Empty;

    [JsonPropertyName("docStatus")]
    public string DocStatus { get; set; } = string.Empty;

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("numAtCard")]
    public string? NumAtCard { get; set; }

    [JsonPropertyName("discountPercent")]
    public decimal DiscountPercent { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("lines")]
    public List<PurchaseInvoiceLineDto> Lines { get; set; } = new();
}

public class PurchaseInvoiceLineDto
{
    [JsonPropertyName("lineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("itemDescription")]
    public string ItemDescription { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("lineTotal")]
    public decimal LineTotal { get; set; }

    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("taxCode")]
    public string? TaxCode { get; set; }
}

public class PurchaseInvoiceListResponse
{
    [JsonPropertyName("invoices")]
    public List<PurchaseInvoiceDto> Invoices { get; set; } = new();

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}

public class CreatePurchaseInvoiceRequest
{
    [JsonPropertyName("cardCode")]
    public string CardCode { get; set; } = string.Empty;

    [JsonPropertyName("docDate")]
    public DateTime? DocDate { get; set; }

    [JsonPropertyName("docDueDate")]
    public DateTime? DocDueDate { get; set; }

    [JsonPropertyName("taxDate")]
    public DateTime? TaxDate { get; set; }

    [JsonPropertyName("docCurrency")]
    public string? DocCurrency { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("numAtCard")]
    public string? NumAtCard { get; set; }

    [JsonPropertyName("discountPercent")]
    public decimal DiscountPercent { get; set; }

    [JsonPropertyName("lines")]
    public List<CreatePurchaseInvoiceLineRequest> Lines { get; set; } = new();
}

public class CreatePurchaseInvoiceLineRequest
{
    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("itemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("taxCode")]
    public string? TaxCode { get; set; }

    [JsonPropertyName("discountPercent")]
    public decimal DiscountPercent { get; set; }
}