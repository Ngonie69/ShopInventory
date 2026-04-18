using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;

/// <summary>
/// Creates a local desktop sale, validates against stock snapshot, and fiscalizes immediately.
/// </summary>
public sealed record CreateDesktopSaleCommand(
    CreateDesktopSaleRequest Request,
    string? CreatedBy
) : IRequest<ErrorOr<DesktopSaleResponseDto>>;

/// <summary>
/// Request DTO for creating a desktop sale.
/// </summary>
public class CreateDesktopSaleRequest
{
    public string? ExternalReferenceId { get; set; }
    public string? SourceSystem { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public string? DocDate { get; set; }
    public int? SalesPersonCode { get; set; }
    public string? NumAtCard { get; set; }
    public string? Comments { get; set; }
    public string? DocCurrency { get; set; }
    public bool Fiscalize { get; set; } = true;
    public string WarehouseCode { get; set; } = string.Empty;
    public string? PaymentMethod { get; set; }
    public string? PaymentReference { get; set; }
    public decimal AmountPaid { get; set; }
    public List<CreateDesktopSaleLineRequest> Lines { get; set; } = new();
}

public class CreateDesktopSaleLineRequest
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string? TaxCode { get; set; }
    public decimal DiscountPercent { get; set; }
    public string? UoMCode { get; set; }
}

/// <summary>
/// Response DTO for a created desktop sale.
/// </summary>
public class DesktopSaleResponseDto
{
    public int SaleId { get; set; }
    public string ExternalReferenceId { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string FiscalizationStatus { get; set; } = string.Empty;
    public string? FiscalReceiptNumber { get; set; }
    public string? FiscalQRCode { get; set; }
    public string? FiscalVerificationCode { get; set; }
    public string? FiscalError { get; set; }
    public DateTime CreatedAt { get; set; }
}
