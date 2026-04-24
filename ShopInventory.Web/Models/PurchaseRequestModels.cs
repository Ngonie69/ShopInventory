using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

public class PurchaseRequestDto
{
    [JsonPropertyName("docEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("docNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("docDate")]
    public DateTime? DocDate { get; set; }

    [JsonPropertyName("requriedDate")]
    public DateTime? RequriedDate { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("requesterName")]
    public string? RequesterName { get; set; }

    [JsonPropertyName("requester")]
    public int? Requester { get; set; }

    [JsonPropertyName("docStatus")]
    public string DocStatus { get; set; } = string.Empty;

    [JsonPropertyName("docTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("lines")]
    public List<PurchaseRequestLineDto> Lines { get; set; } = new();
}

public class PurchaseRequestLineDto
{
    [JsonPropertyName("lineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("itemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("openQuantity")]
    public decimal OpenQuantity { get; set; }

    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("lineVendor")]
    public string? LineVendor { get; set; }

    [JsonPropertyName("requiredDate")]
    public DateTime? RequiredDate { get; set; }

    [JsonPropertyName("uoMCode")]
    public string? UoMCode { get; set; }
}

public class PurchaseRequestListResponse
{
    [JsonPropertyName("requests")]
    public List<PurchaseRequestDto> Requests { get; set; } = new();

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

public class CreatePurchaseRequestRequest
{
    [JsonPropertyName("docDate")]
    public DateTime? DocDate { get; set; }

    [JsonPropertyName("requriedDate")]
    public DateTime? RequriedDate { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("requester")]
    public int? Requester { get; set; }

    [JsonPropertyName("lines")]
    public List<CreatePurchaseRequestLineRequest> Lines { get; set; } = new();
}

public class CreatePurchaseRequestLineRequest
{
    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("itemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("requiredDate")]
    public DateTime? RequiredDate { get; set; }

    [JsonPropertyName("lineVendor")]
    public string? LineVendor { get; set; }

    [JsonPropertyName("uoMCode")]
    public string? UoMCode { get; set; }

    [JsonPropertyName("uoMEntry")]
    public int? UoMEntry { get; set; }
}