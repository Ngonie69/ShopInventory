using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

public class GoodsReceiptPurchaseOrderDto
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
    public List<GoodsReceiptPurchaseOrderLineDto>? Lines { get; set; }
}

public class GoodsReceiptPurchaseOrderLineDto
{
    public int LineNum { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal LineTotal { get; set; }
    public string? WarehouseCode { get; set; }
    public string? TaxCode { get; set; }
    public string? UoMCode { get; set; }
    public int? BaseEntry { get; set; }
    public int? BaseLine { get; set; }
    public int? BaseType { get; set; }
}

public class CreateGoodsReceiptPurchaseOrderRequest
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
    public List<CreateGoodsReceiptPurchaseOrderLineRequest> Lines { get; set; } = new();
}

public class CreateGoodsReceiptPurchaseOrderLineRequest
{
    [Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = null!;

    public string? ItemDescription { get; set; }

    [Range(0.0001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }
    public string? WarehouseCode { get; set; }
    public string? TaxCode { get; set; }
    public string? UoMCode { get; set; }
    public int? UoMEntry { get; set; }
    public int? BaseEntry { get; set; }
    public int? BaseLine { get; set; }
    public int BaseType { get; set; } = 22;
}

public class GoodsReceiptPurchaseOrderListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasMore { get; set; }
    public List<GoodsReceiptPurchaseOrderDto>? GoodsReceipts { get; set; }
}