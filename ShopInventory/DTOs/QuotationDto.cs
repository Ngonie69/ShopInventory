using System.ComponentModel.DataAnnotations;
using ShopInventory.Models.Entities;

namespace ShopInventory.DTOs;

#region Quotation DTOs

public class QuotationDto
{
    public int Id { get; set; }
    public int? SAPDocEntry { get; set; }
    public int? SAPDocNum { get; set; }
    public string QuotationNumber { get; set; } = null!;
    public DateTime QuotationDate { get; set; }
    public DateTime? ValidUntil { get; set; }
    public string CardCode { get; set; } = null!;
    public string? CardName { get; set; }
    public string? CustomerRefNo { get; set; }
    public string? ContactPerson { get; set; }
    public QuotationStatus Status { get; set; }
    public string StatusName => Status.ToString();
    public string? Comments { get; set; }
    public string? TermsAndConditions { get; set; }
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
    public int? SalesOrderId { get; set; }
    public bool IsSynced { get; set; }
    public bool IsExpired => ValidUntil.HasValue && ValidUntil.Value < DateTime.UtcNow && Status != QuotationStatus.Converted && Status != QuotationStatus.Cancelled;
    public List<QuotationLineDto> Lines { get; set; } = new();
}

public class QuotationLineDto
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
    public string? UoMCode { get; set; }
}

public class CreateQuotationRequest
{
    public DateTime? ValidUntil { get; set; }

    [Required(ErrorMessage = "Customer code is required")]
    public string CardCode { get; set; } = null!;

    public string? CardName { get; set; }
    public string? CustomerRefNo { get; set; }
    public string? ContactPerson { get; set; }
    public string? Comments { get; set; }
    public string? TermsAndConditions { get; set; }
    public int? SalesPersonCode { get; set; }
    public string? SalesPersonName { get; set; }
    public string? Currency { get; set; } = "USD";
    public decimal DiscountPercent { get; set; }
    public string? ShipToAddress { get; set; }
    public string? BillToAddress { get; set; }
    public string? WarehouseCode { get; set; }

    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreateQuotationLineRequest> Lines { get; set; } = new();
}

public class CreateQuotationLineRequest
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
}

public class UpdateQuotationStatusRequest
{
    [Required]
    public QuotationStatus Status { get; set; }

    public string? Comments { get; set; }
}

public class QuotationListResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<QuotationDto> Quotations { get; set; } = new();
}

#endregion
