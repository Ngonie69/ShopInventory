using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Commands.UpdateProductStatusGlobal;

public sealed record UpdateProductStatusGlobalCommand(
    UpdateMerchandiserProductStatusRequest Request,
    string Username
) : IRequest<ErrorOr<int>>;
