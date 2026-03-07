using System.ComponentModel.DataAnnotations;
using ShopInventory.Models.Entities;

namespace ShopInventory.DTOs;

#region Purchase Order DTOs

/// <summary>
/// DTO for Purchase Order response
/// </summary>
public class PurchaseOrderDto
{
    public int Id { get; set; }
    public int? SAPDocEntry { get; set; }
    public int? SAPDocNum { get; set; }
    public string OrderNumber { get; set; } = null!;
    public DateTime OrderDate { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string CardCode { get; set; } = null!;
    public string? CardName { get; set; }
    public string? SupplierRefNo { get; set; }
    public PurchaseOrderStatus Status { get; set; }
    public string StatusName => Status.ToString();
    public string? Comments { get; set; }
    public int? BuyerCode { get; set; }
    public string? BuyerName { get; set; }
    public string? Currency { get; set; }
    public decimal ExchangeRate { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal DocTotal { get; set; }
    public string? ShipToAddress { get; set; }
    public string? BillToAddress { get; set; }
    public string? WarehouseCode { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovedByUserName { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsSynced { get; set; }
    public List<PurchaseOrderLineDto> Lines { get; set; } = new();
}

/// <summary>
/// DTO for Purchase Order Line
/// </summary>
public class PurchaseOrderLineDto
{
    public int Id { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = null!;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuantityReceived { get; set; }
    public decimal QuantityRemaining => Quantity - QuantityReceived;
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal LineTotal { get; set; }
    public string? WarehouseCode { get; set; }
    public string? UoMCode { get; set; }
    public string? BatchNumber { get; set; }
}

/// <summary>
/// Request to create a purchase order
/// </summary>
public class CreatePurchaseOrderRequest
{
    public DateTime? DeliveryDate { get; set; }

    [Required(ErrorMessage = "Supplier code is required")]
    public string CardCode { get; set; } = null!;

    public string? CardName { get; set; }
    public string? SupplierRefNo { get; set; }
    public string? Comments { get; set; }
    public int? BuyerCode { get; set; }
    public string? BuyerName { get; set; }
    public string? Currency { get; set; } = "USD";
    public decimal DiscountPercent { get; set; }
    public string? ShipToAddress { get; set; }
    public string? BillToAddress { get; set; }
    public string? WarehouseCode { get; set; }

    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreatePurchaseOrderLineRequest> Lines { get; set; } = new();
}

/// <summary>
/// Request to create a purchase order line
/// </summary>
public class CreatePurchaseOrderLineRequest
{
    [Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = null!;

    public string? ItemDescription { get; set; }

    [Range(0.0001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Unit price cannot be negative")]
    public decimal UnitPrice { get; set; }

    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public string? WarehouseCode { get; set; }
    public string? UoMCode { get; set; }
    public string? BatchNumber { get; set; }
}

/// <summary>
/// Request to update purchase order status
/// </summary>
public class UpdatePurchaseOrderStatusRequest
{
    [Required]
    public PurchaseOrderStatus Status { get; set; }

    public string? Comments { get; set; }
}

/// <summary>
/// Request to receive items against a purchase order
/// </summary>
public class ReceivePurchaseOrderRequest
{
    [Required(ErrorMessage = "At least one line must be received")]
    [MinLength(1, ErrorMessage = "At least one line must be received")]
    public List<ReceivePurchaseOrderLineRequest> Lines { get; set; } = new();

    public string? Comments { get; set; }
    public string? WarehouseCode { get; set; }
}

/// <summary>
/// Request to receive a specific line item
/// </summary>
public class ReceivePurchaseOrderLineRequest
{
    public int LineNum { get; set; }

    [Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = null!;

    [Range(0.0001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal QuantityReceived { get; set; }

    public string? WarehouseCode { get; set; }
    public string? BatchNumber { get; set; }
}

/// <summary>
/// Purchase order list response
/// </summary>
public class PurchaseOrderListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<PurchaseOrderDto> Orders { get; set; } = new();
}

#endregion
