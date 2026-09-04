using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Shops.Queries.GetShops;

/// <summary>
/// Every shop, newest-named first.
/// </summary>
/// <param name="IncludeInactive">
/// Closed shops are excluded by default. An administrator managing shops asks for them; a picker
/// offering a shop to assign an operator to must not, or a till could be opened on a closed counter.
/// </param>
public sealed record GetShopsQuery(
    bool IncludeInactive = false
) : IRequest<ErrorOr<List<ShopDto>>>;
