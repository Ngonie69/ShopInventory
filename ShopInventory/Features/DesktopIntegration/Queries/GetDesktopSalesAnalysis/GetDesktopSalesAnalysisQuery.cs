using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// A breakdown of till takings over a period: by payment method, day, hour, shop, business partner,
/// source, operator and item.
/// </summary>
/// <remarks>
/// Scoped exactly as the sales list is, and for the same reason. <c>CallerUserId</c> decides whose shops
/// may be read, a caller confined to one shop is narrowed to it when no warehouse is named, and naming
/// another shop's is refused.
///
/// The dates are the sales' business dates (<c>DocDate</c>), both inclusive. Omitting both analyses today
/// in CAT; omitting only the end runs to today.
///
/// <c>SourceSystem</c> selects one source, or null for every source. Unlike the list, null includes
/// <c>SaleSourceSystems.VanSalesOnline</c>: this report reads neither reservations nor SAP invoices, so an
/// online van sale's receipt row is its only record here, and it is counted once. <c>Business</c> narrows
/// by <c>SaleBusinesses</c>, whose own rules still leave those rows out.
///
/// <c>PaymentMethod</c> confines every figure to one tender, matched on its reporting name, so a till's
/// own spelling ("ecocash") and the canonical one read the same sales. "Not recorded" selects the sales
/// no tender was stored for.
///
/// <c>Business</c> confines it to one of <c>SaleBusinesses</c> — shops, vending or vans — on top of the
/// source scope; null reads them all.
/// </remarks>
public sealed record GetDesktopSalesAnalysisQuery(
    Guid CallerUserId,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    string? WarehouseCode = null,
    string? SourceSystem = null,
    string? PaymentMethod = null,
    string? Business = null
) : IRequest<ErrorOr<DesktopSalesAnalysisResult>>;
