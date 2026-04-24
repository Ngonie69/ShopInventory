using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class PurchaseQuotationDto
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
    public decimal DocTotal { get; set; }
    public decimal VatSum { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TotalDiscount { get; set; }
    public string? DocCurrency { get; set; }
    public string? BillToAddress { get; set; }
    public string? ShipToAddress { get; set; }
    public string? Source { get; set; }
    public List<PurchaseQuotationLineDto>? Lines { get; set; }
}

public class PurchaseQuotationLineDto
{
    public int LineNum { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public string? WarehouseCode { get; set; }
    public decimal DiscountPercent { get; set; }
    public string? TaxCode { get; set; }
    public string? UoMCode { get; set; }
}

public class CreatePurchaseQuotationRequest
{
    [Required(ErrorMessage = "Supplier code is required")]
    public string CardCode { get; set; } = null!;

    public DateTime? DocDate { get; set; }
    public DateTime? DocDueDate { get; set; }
    public string? NumAtCard { get; set; }
    public string? Comments { get; set; }
    public string? DocCurrency { get; set; }

    [Range(0, 100, ErrorMessage = "Discount percent must be between 0 and 100")]
    public decimal DiscountPercent { get; set; }

    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreatePurchaseQuotationLineRequest> Lines { get; set; } = new();
}

public class CreatePurchaseQuotationLineRequest
{
    [Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = null!;

    public string? ItemDescription { get; set; }

    [Range(0.0001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    [Range(0.0001, double.MaxValue, ErrorMessage = "Unit price must be greater than zero")]
    public decimal UnitPrice { get; set; }

    [Range(0, 100, ErrorMessage = "Discount percent must be between 0 and 100")]
    public decimal DiscountPercent { get; set; }

    public string? WarehouseCode { get; set; }
    public string? TaxCode { get; set; }
    public string? UoMCode { get; set; }
    public int? UoMEntry { get; set; }
}

public class PurchaseQuotationListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasMore { get; set; }
    public List<PurchaseQuotationDto>? Quotations { get; set; }
}