using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Commands.RemoveProductsGlobal;

public sealed record RemoveProductsGlobalCommand(
    AssignMerchandiserProductsRequest Request
) : IRequest<ErrorOr<Success>>;
