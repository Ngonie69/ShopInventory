using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Commands.RemoveProducts;

public sealed record RemoveProductsCommand(
    Guid UserId,
    AssignMerchandiserProductsRequest Request
) : IRequest<ErrorOr<Success>>;
