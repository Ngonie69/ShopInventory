using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Products.Queries.GetVanSaleCatalogue;

/// <summary>
/// Every item a van is approved to carry — the item master rows flagged <c>U_VanSale = 'Yes'</c>.
/// </summary>
/// <remarks>
/// Takes no warehouse, on purpose. This answers what a van may be sent, which is a different question
/// from what it is carrying, and the caller is a stock transfer request: the items worth asking the
/// depot for are the ones the van has none of.
/// </remarks>
public sealed record GetVanSaleCatalogueQuery() : IRequest<ErrorOr<ProductsListResponseDto>>;
