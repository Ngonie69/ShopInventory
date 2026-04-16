using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Commands.UpdateProductStatus;

public sealed record UpdateProductStatusCommand(
    Guid UserId,
    UpdateMerchandiserProductStatusRequest Request,
    string Username
) : IRequest<ErrorOr<int>>;
