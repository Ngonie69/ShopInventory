using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Prices.Commands.SyncPriceLists;

public sealed record SyncPriceListsCommand() : IRequest<ErrorOr<object>>;
