using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Users.Commands.UpdateUser;

public sealed record UpdateUserCommand(
    Guid Id,
    UpdateUserRequest Request
) : IRequest<ErrorOr<UserDto>>;
