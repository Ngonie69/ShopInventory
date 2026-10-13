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
/// <c>SourceSystem</c> selects one source. Null reads the counters — every source but the two a van
/// handset writes (<c>SaleSourceSystems.VanSaleSources</c>). Van takings are analysed by
/// <c>GetVanSalesAnalysisQuery</c>, which reads the online sales this table holds only a receipt for.
/// Naming a van source still reads it.
///
/// <c>PaymentMethod</c> confines every figure to one tender, matched on its reporting name, so a till's
/// own spelling ("ecocash") and the canonical one read the same sales. "Not recorded" selects the sales
/// no tender was stored for.
///
/// <c>Business</c> confines it to one of <c>SaleBusinesses</c> — shops, vending or vans — on top of the
/// source scope; null reads shops and vending together. No counter report asks for the vans.
///
/// <c>CardCode</c> confines every figure to the sales made as one business partner, matched exactly as the
/// management report matches it. It narrows within the caller's scope and never widens it: a shop-confined
/// caller naming a partner reads that partner's sales at their own shop only.
/// </remarks>
public sealed record GetDesktopSalesAnalysisQuery(
    Guid CallerUserId,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    string? WarehouseCode = null,
    string? SourceSystem = null,
    string? PaymentMethod = null,
    string? Business = null,
    string? CardCode = null
) : IRequest<ErrorOr<DesktopSalesAnalysisResult>>;
