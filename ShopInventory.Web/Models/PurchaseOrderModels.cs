using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

/// <summary>
/// Purchase Order status enum
/// </summary>
public enum PurchaseOrderStatus
{
    Draft = 0,
    Pending = 1,
    Approved = 2,
    PartiallyReceived = 3,
    Received = 4,
    Cancelled = 5,
    OnHold = 6
}

/// <summary>
/// DTO for Purchase Order response
/// </summary>
public class PurchaseOrderDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("sapDocEntry")]
    public int? SAPDocEntry { get; set; }

    [JsonPropertyName("sapDocNum")]
    public int? SAPDocNum { get; set; }

    [JsonPropertyName("orderNumber")]
    public string OrderNumber { get; set; } = null!;

    [JsonPropertyName("orderDate")]
    public DateTime OrderDate { get; set; }

    [JsonPropertyName("deliveryDate")]
    public DateTime? DeliveryDate { get; set; }

    [JsonPropertyName("cardCode")]
    public string CardCode { get; set; } = null!;

    [JsonPropertyName("cardName")]
    public string? CardName { get; set; }

    [JsonPropertyName("supplierRefNo")]
    public string? SupplierRefNo { get; set; }

    [JsonPropertyName("status")]
    public PurchaseOrderStatus Status { get; set; }

    public string StatusName => Status.ToString();

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("buyerCode")]
    public int? BuyerCode { get; set; }

    [JsonPropertyName("buyerName")]
    public string? BuyerName { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("exchangeRate")]
    public decimal ExchangeRate { get; set; }

    [JsonPropertyName("subTotal")]
    public decimal SubTotal { get; set; }

    [JsonPropertyName("taxAmount")]
    public decimal TaxAmount { get; set; }

    [JsonPropertyName("discountPercent")]
    public decimal DiscountPercent { get; set; }

    [JsonPropertyName("discountAmount")]
    public decimal DiscountAmount { get; set; }

    [JsonPropertyName("docTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("shipToAddress")]
    public string? ShipToAddress { get; set; }

    [JsonPropertyName("billToAddress")]
    public string? BillToAddress { get; set; }

    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("createdByUserId")]
    public Guid? CreatedByUserId { get; set; }

    [JsonPropertyName("createdByUserName")]
    public string? CreatedByUserName { get; set; }

    [JsonPropertyName("approvedByUserId")]
    public Guid? ApprovedByUserId { get; set; }

    [JsonPropertyName("approvedByUserName")]
    public string? ApprovedByUserName { get; set; }

    [JsonPropertyName("approvedDate")]
    public DateTime? ApprovedDate { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("isSynced")]
    public bool IsSynced { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("lines")]
    public List<PurchaseOrderLineDto> Lines { get; set; } = new();
}

/// <summary>
/// DTO for Purchase Order Line
/// </summary>
public class PurchaseOrderLineDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("lineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = null!;

    [JsonPropertyName("itemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("quantityReceived")]
    public decimal QuantityReceived { get; set; }

    public decimal QuantityRemaining => Quantity - QuantityReceived;

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("discountPercent")]
    public decimal DiscountPercent { get; set; }

    [JsonPropertyName("taxPercent")]
    public decimal TaxPercent { get; set; }

    [JsonPropertyName("lineTotal")]
    public decimal LineTotal { get; set; }

    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("uoMCode")]
    public string? UoMCode { get; set; }

    [JsonPropertyName("batchNumber")]
    public string? BatchNumber { get; set; }
}

/// <summary>
/// Request to create a purchase order
/// </summary>
public class CreatePurchaseOrderRequest
{
    public DateTime? DeliveryDate { get; set; }
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
    public List<CreatePurchaseOrderLineRequest> Lines { get; set; } = new();
}

/// <summary>
/// Request to create a purchase order line
/// </summary>
public class CreatePurchaseOrderLineRequest
{
    public string ItemCode { get; set; } = null!;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
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
    public PurchaseOrderStatus Status { get; set; }
    public string? Comments { get; set; }
}

/// <summary>
/// Request to receive items against a purchase order
/// </summary>
public class ReceivePurchaseOrderRequest
{
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
    public string ItemCode { get; set; } = null!;
    public decimal QuantityReceived { get; set; }
    public string? WarehouseCode { get; set; }
    public string? BatchNumber { get; set; }
}

/// <summary>
/// Purchase order list response
/// </summary>
public class PurchaseOrderListResponse
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("orders")]
    public List<PurchaseOrderDto> Orders { get; set; } = new();
}
