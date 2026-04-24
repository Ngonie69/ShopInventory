using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

public class GoodsReceiptPurchaseOrderDto
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
    public DateTime? DocDate { get; set; }

    [JsonPropertyName("docDueDate")]
    public DateTime? DocDueDate { get; set; }

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

    [JsonPropertyName("lines")]
    public List<GoodsReceiptPurchaseOrderLineDto> Lines { get; set; } = new();
}

public class GoodsReceiptPurchaseOrderLineDto
{
    [JsonPropertyName("lineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("itemDescription")]
    public string ItemDescription { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("lineTotal")]
    public decimal LineTotal { get; set; }

    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("taxCode")]
    public string? TaxCode { get; set; }

    [JsonPropertyName("uoMCode")]
    public string? UoMCode { get; set; }

    [JsonPropertyName("baseEntry")]
    public int? BaseEntry { get; set; }

    [JsonPropertyName("baseLine")]
    public int? BaseLine { get; set; }

    [JsonPropertyName("baseType")]
    public int? BaseType { get; set; }
}

public class GoodsReceiptPurchaseOrderListResponse
{
    [JsonPropertyName("goodsReceipts")]
    public List<GoodsReceiptPurchaseOrderDto> GoodsReceipts { get; set; } = new();

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

public class CreateGoodsReceiptPurchaseOrderRequest
{
    [JsonPropertyName("cardCode")]
    public string CardCode { get; set; } = string.Empty;

    [JsonPropertyName("docDate")]
    public DateTime? DocDate { get; set; }

    [JsonPropertyName("docDueDate")]
    public DateTime? DocDueDate { get; set; }

    [JsonPropertyName("docCurrency")]
    public string? DocCurrency { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("numAtCard")]
    public string? NumAtCard { get; set; }

    [JsonPropertyName("discountPercent")]
    public decimal DiscountPercent { get; set; }

    [JsonPropertyName("lines")]
    public List<CreateGoodsReceiptPurchaseOrderLineRequest> Lines { get; set; } = new();
}

public class CreateGoodsReceiptPurchaseOrderLineRequest
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

    [JsonPropertyName("uoMCode")]
    public string? UoMCode { get; set; }

    [JsonPropertyName("uoMEntry")]
    public int? UoMEntry { get; set; }

    [JsonPropertyName("baseEntry")]
    public int? BaseEntry { get; set; }

    [JsonPropertyName("baseLine")]
    public int? BaseLine { get; set; }

    [JsonPropertyName("baseType")]
    public int BaseType { get; set; } = 22;
}