using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Prices.Queries.GetPricesByPriceList;

public sealed record GetPricesByPriceListQuery(
    int PriceListNum,
    bool ForceRefresh
) : IRequest<ErrorOr<ItemPricesByListResponseDto>>;
