using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.UserManagement.Commands.CreateMerchandiserAccount;

public sealed record CreateMerchandiserAccountCommand(
    CreateMerchandiserAccountFormModel Request
) : IRequest<ErrorOr<string>>;