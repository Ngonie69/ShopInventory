using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Prices.Queries.GetItemPriceFromList;

public sealed record GetItemPriceFromListQuery(
    int PriceListNum,
    string ItemCode
) : IRequest<ErrorOr<ItemPriceByListDto>>;
