using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;

public sealed record GetDesktopSalesQuery(
    string? WarehouseCode = null,
    string? CardCode = null,
    string? ConsolidationStatus = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int Page = 1,
    int PageSize = 50
) : IRequest<ErrorOr<DesktopSalesListResult>>;

public sealed record DesktopSalesListResult(
    List<DesktopSaleListItemDto> Sales,
    int TotalCount,
    int Page,
    int PageSize,
    bool HasMore
);

public sealed record DesktopSaleListItemDto(
    int Id,
    string ExternalReferenceId,
    string? SourceSystem,
    string CardCode,
    string? CardName,
    DateTime DocDate,
    decimal TotalAmount,
    decimal VatAmount,
    string Currency,
    string FiscalizationStatus,
    string? FiscalReceiptNumber,
    string ConsolidationStatus,
    int? ConsolidationId,
    string WarehouseCode,
    string? PaymentMethod,
    string? PaymentReference,
    decimal AmountPaid,
    string? CreatedBy,
    DateTime CreatedAt,
    List<DesktopSaleLineItemDto> Lines
);

public sealed record DesktopSaleLineItemDto(
    int LineNum,
    string ItemCode,
    string? ItemDescription,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    string WarehouseCode,
    string? TaxCode,
    decimal DiscountPercent
);
