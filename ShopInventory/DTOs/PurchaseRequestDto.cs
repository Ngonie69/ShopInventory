using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class PurchaseRequestDto
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string? DocDate { get; set; }

    [JsonPropertyName("requriedDate")]
    public string? RequriedDate { get; set; }

    public string? Comments { get; set; }
    public string? RequesterName { get; set; }
    public int? Requester { get; set; }
    public string? DocStatus { get; set; }
    public decimal DocTotal { get; set; }
    public string? Source { get; set; }
    public List<PurchaseRequestLineDto>? Lines { get; set; }
}

public class PurchaseRequestLineDto
{
    public int LineNum { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal OpenQuantity { get; set; }
    public string? WarehouseCode { get; set; }
    public string? LineVendor { get; set; }
    public string? RequiredDate { get; set; }
    public string? UoMCode { get; set; }
}

public class CreatePurchaseRequestRequest
{
    public DateTime? DocDate { get; set; }

    [JsonPropertyName("RequriedDate")]
    public DateTime? RequiredDate { get; set; }

    public string? Comments { get; set; }
    public int? Requester { get; set; }

    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreatePurchaseRequestLineRequest> Lines { get; set; } = new();
}

public class CreatePurchaseRequestLineRequest
{
    [Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = null!;

    public string? ItemDescription { get; set; }

    [Range(0.0001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    public string? WarehouseCode { get; set; }

    [JsonPropertyName("RequiredDate")]
    public DateTime? RequiredDate { get; set; }

    public string? LineVendor { get; set; }
    public string? UoMCode { get; set; }
    public int? UoMEntry { get; set; }
}

public class PurchaseRequestListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasMore { get; set; }
    public List<PurchaseRequestDto>? Requests { get; set; }
}