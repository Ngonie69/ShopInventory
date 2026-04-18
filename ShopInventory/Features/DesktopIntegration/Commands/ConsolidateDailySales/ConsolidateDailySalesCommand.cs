using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;

/// <summary>
/// Consolidates pending desktop sales by business partner and posts to SAP.
/// </summary>
public sealed record ConsolidateDailySalesCommand(
    DateTime? ConsolidationDate = null
) : IRequest<ErrorOr<ConsolidateDailySalesResult>>;

public sealed record ConsolidateDailySalesResult(
    DateTime ConsolidationDate,
    int TotalSalesProcessed,
    int SuccessfulPostings,
    int FailedPostings,
    List<ConsolidationGroupResult> Groups
);

public sealed record ConsolidationGroupResult(
    string CardCode,
    string? CardName,
    int SaleCount,
    decimal TotalAmount,
    int? SapDocNum,
    int? PaymentSapDocNum,
    string Status,
    string? Error
);
