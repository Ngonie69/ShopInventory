using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement.Commands.UpdateUser;

public sealed record UpdateUserCommand(
    Guid Id,
    UpdateUserDetailRequest Request
) : IRequest<ErrorOr<Success>>;
