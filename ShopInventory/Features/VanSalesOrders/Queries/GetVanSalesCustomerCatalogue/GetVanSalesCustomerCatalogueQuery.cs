using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerCatalogue;

/// <summary>
/// What the signed-in shop can order, priced, with a stock indication.
/// </summary>
/// <remarks>
/// The account comes from the caller's token. It is needed not to filter the item list — every
/// customer sees the same products on the same price list — but to find the depot the shop's van
/// loads from, which is what the stock bands describe.
/// </remarks>
public sealed record GetVanSalesCustomerCatalogueQuery(
    int AccountId
) : IRequest<ErrorOr<VanSalesCatalogueResult>>;
