using System.ComponentModel.DataAnnotations;
using ShopInventory.Models.Entities;

namespace ShopInventory.DTOs;

#region Sales Order DTOs

/// <summary>
/// DTO for Sales Order response
/// </summary>
public class SalesOrderDto
{
    public int Id { get; set; }
    public int? SAPDocEntry { get; set; }
    public int? SAPDocNum { get; set; }
    public string OrderNumber { get; set; } = null!;
    public DateTime OrderDate { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string CardCode { get; set; } = null!;
    public string? CardName { get; set; }
    public string? CustomerRefNo { get; set; }
    public SalesOrderStatus Status { get; set; }
    public string StatusName => Status.ToString();
    public string? Comments { get; set; }
    public int? SalesPersonCode { get; set; }
    public string? SalesPersonName { get; set; }
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
    public int? InvoiceId { get; set; }
    public bool IsSynced { get; set; }
    public List<SalesOrderLineDto> Lines { get; set; } = new();
}

/// <summary>
/// DTO for Sales Order Line
/// </summary>
public class SalesOrderLineDto
{
    public int Id { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = null!;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuantityFulfilled { get; set; }
    public decimal QuantityRemaining => Quantity - QuantityFulfilled;
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal LineTotal { get; set; }
    public string? WarehouseCode { get; set; }
    public string? UoMCode { get; set; }
    public string? BatchNumber { get; set; }
}

/// <summary>
/// Request to create a sales order
/// </summary>
public class CreateSalesOrderRequest
{
    public DateTime? DeliveryDate { get; set; }

    [Required(ErrorMessage = "Customer code is required")]
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

    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreateSalesOrderLineRequest> Lines { get; set; } = new();
}

/// <summary>
/// Request to create a sales order line
/// </summary>
public class CreateSalesOrderLineRequest
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
/// Request to update sales order status
/// </summary>
public class UpdateSalesOrderStatusRequest
{
    [Required]
    public SalesOrderStatus Status { get; set; }

    public string? Comments { get; set; }
}

/// <summary>
/// Sales order list response
/// </summary>
public class SalesOrderListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<SalesOrderDto> Orders { get; set; } = new();
}

#endregion

#region Credit Note DTOs

/// <summary>
/// DTO for Credit Note response
/// </summary>
public class CreditNoteDto
{
    public int Id { get; set; }
    public int? SAPDocEntry { get; set; }
    public int? SAPDocNum { get; set; }
    public string CreditNoteNumber { get; set; } = null!;
    public DateTime CreditNoteDate { get; set; }
    public string CardCode { get; set; } = null!;
    public string? CardName { get; set; }
    public CreditNoteType Type { get; set; }
    public string TypeName => Type.ToString();
    public CreditNoteStatus Status { get; set; }
    public string StatusName => Status.ToString();
    public int? OriginalInvoiceId { get; set; }
    public int? OriginalInvoiceDocEntry { get; set; }
    public string? Reason { get; set; }
    public string? Comments { get; set; }
    public string? Currency { get; set; }
    public decimal ExchangeRate { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal DocTotal { get; set; }
    public decimal AppliedAmount { get; set; }
    public decimal Balance { get; set; }
    public bool RestockItems { get; set; }
    public string? RestockWarehouseCode { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovedByUserName { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsSynced { get; set; }
    public List<CreditNoteLineDto> Lines { get; set; } = new();
}

/// <summary>
/// DTO for Credit Note Line
/// </summary>
public class CreditNoteLineDto
{
    public int Id { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = null!;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal LineTotal { get; set; }
    public string? WarehouseCode { get; set; }
    public string? ReturnReason { get; set; }
    public string? BatchNumber { get; set; }
    public bool IsRestocked { get; set; }
}

/// <summary>
/// Request to create a credit note
/// </summary>
public class CreateCreditNoteRequest
{
    [Required(ErrorMessage = "Customer code is required")]
    public string CardCode { get; set; } = null!;

    public string? CardName { get; set; }

    public CreditNoteType Type { get; set; } = CreditNoteType.Return;

    /// <summary>
    /// Local database Invoice ID (nullable - may not exist locally)
    /// </summary>
    public int? OriginalInvoiceId { get; set; }

    /// <summary>
    /// SAP DocEntry of the original invoice
    /// </summary>
    public int? OriginalInvoiceDocEntry { get; set; }

    [Required(ErrorMessage = "Reason is required")]
    public string Reason { get; set; } = null!;

    public string? Comments { get; set; }
    public string? Currency { get; set; } = "USD";
    public bool RestockItems { get; set; } = true;
    public string? RestockWarehouseCode { get; set; }

    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreateCreditNoteLineRequest> Lines { get; set; } = new();
}

/// <summary>
/// Request to create a credit note line
/// </summary>
public class CreateCreditNoteLineRequest
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
    public string? ReturnReason { get; set; }
    public string? BatchNumber { get; set; }
    public int? OriginalInvoiceLineId { get; set; }

    /// <summary>
    /// Batch numbers for batch-managed items (required for returns)
    /// </summary>
    public List<CreditNoteBatchRequest>? BatchNumbers { get; set; }
}

/// <summary>
/// Batch number details for credit note line
/// </summary>
public class CreditNoteBatchRequest
{
    public string? BatchNumber { get; set; }
    public decimal Quantity { get; set; }
}

/// <summary>
/// Credit note list response
/// </summary>
public class CreditNoteListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<CreditNoteDto> CreditNotes { get; set; } = new();
}

#endregion
