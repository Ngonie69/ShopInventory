using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Prices.Commands.SyncPriceCatalog;

public sealed record SyncPriceCatalogCommand() : IRequest<ErrorOr<object>>;