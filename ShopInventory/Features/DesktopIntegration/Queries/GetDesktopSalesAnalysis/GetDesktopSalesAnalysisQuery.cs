using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// A breakdown of till takings over a period: by payment method, day, hour, shop, source, operator and
/// item.
/// </summary>
/// <remarks>
/// Scoped exactly as the sales list is, and for the same reason. <c>CallerUserId</c> decides whose shops
/// may be read, a caller confined to one shop is narrowed to it when no warehouse is named, and naming
/// another shop's is refused.
///
/// The dates are the sales' business dates (<c>DocDate</c>), both inclusive. Omitting both analyses today
/// in CAT; omitting only the end runs to today.
///
/// <c>SourceSystem</c> selects one source, or null for the default scope, which leaves out
/// <c>SaleSourceSystems.VanSalesOnline</c> — those rows carry receipts for sales already counted as their
/// SAP invoice, and adding them in would count the money twice.
/// </remarks>
public sealed record GetDesktopSalesAnalysisQuery(
    Guid CallerUserId,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    string? WarehouseCode = null,
    string? SourceSystem = null
) : IRequest<ErrorOr<DesktopSalesAnalysisResult>>;
