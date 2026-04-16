using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Users.Commands.CreateUser;

public sealed record CreateUserCommand(
    CreateUserRequest Request
) : IRequest<ErrorOr<UserCreatedResponseDto>>;
