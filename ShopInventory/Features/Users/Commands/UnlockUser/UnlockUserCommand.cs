using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Users.Commands.UnlockUser;

public sealed record UnlockUserCommand(
    Guid Id,
    UnlockUserRequest? Request
) : IRequest<ErrorOr<Success>>;
