using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

/// <summary>
/// Sales Order status enum
/// </summary>
public enum SalesOrderStatus
{
    Draft = 0,
    Pending = 1,
    Approved = 2,
    PartiallyFulfilled = 3,
    Fulfilled = 4,
    Invoiced = 5,
    Cancelled = 6,
    OnHold = 7
}

/// <summary>
/// DTO for Sales Order response
/// </summary>
public class SalesOrderDto
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

    [JsonPropertyName("customerRefNo")]
    public string? CustomerRefNo { get; set; }

    [JsonPropertyName("status")]
    public SalesOrderStatus Status { get; set; }

    public string StatusName => Status.ToString();

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("salesPersonCode")]
    public int? SalesPersonCode { get; set; }

    [JsonPropertyName("salesPersonName")]
    public string? SalesPersonName { get; set; }

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

    [JsonPropertyName("invoiceId")]
    public int? InvoiceId { get; set; }

    [JsonPropertyName("isSynced")]
    public bool IsSynced { get; set; }

    [JsonPropertyName("lines")]
    public List<SalesOrderLineDto> Lines { get; set; } = new();
}

/// <summary>
/// DTO for Sales Order Line
/// </summary>
public class SalesOrderLineDto
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

    [JsonPropertyName("quantityFulfilled")]
    public decimal QuantityFulfilled { get; set; }

    public decimal QuantityRemaining => Quantity - QuantityFulfilled;

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
/// Request to create a sales order
/// </summary>
public class CreateSalesOrderRequest
{
    public DateTime? DeliveryDate { get; set; }
    public string CardCode { get; set; } = null!;
    public string? CardName { get; set; }
    public string? CustomerRefNo { get; set; }
    public string? Comments { get; set; }
    public int? SalesPersonCode { get; set; }
    public string? SalesPersonName { get; set; }
    public string? Currency { get; set; } = "USD";
    public decimal DiscountPercent { get; set; }
    public string? ShipToAddress { get; set; }
    public string? BillToAddress { get; set; }
    public string? WarehouseCode { get; set; }
    public List<CreateSalesOrderLineRequest> Lines { get; set; } = new();
}

/// <summary>
/// Request to create a sales order line
/// </summary>
public class CreateSalesOrderLineRequest
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
/// Request to update sales order status
/// </summary>
public class UpdateSalesOrderStatusRequest
{
    public SalesOrderStatus Status { get; set; }
    public string? Comments { get; set; }
}

/// <summary>
/// Sales order list response
/// </summary>
public class SalesOrderListResponse
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
    public List<SalesOrderDto> Orders { get; set; } = new();
}
