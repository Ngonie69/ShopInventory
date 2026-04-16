using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Commands.Register;

public sealed record RegisterCommand(
    string Username,
    string? Email,
    string Password,
    string Role,
    string AdminUsername
) : IRequest<ErrorOr<UserInfo>>;
