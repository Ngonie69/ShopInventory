using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Prices.Commands.SyncItemPricesForPriceList;

public sealed record SyncItemPricesForPriceListCommand(
    int PriceListNum
) : IRequest<ErrorOr<object>>;
