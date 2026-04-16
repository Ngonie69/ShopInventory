using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement.Commands.CreateUser;

public sealed record CreateUserCommand(
    CreateUserDetailRequest Request
) : IRequest<ErrorOr<UserDetailDto>>;
