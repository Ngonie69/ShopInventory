using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Commands.Login;

public sealed record LoginCommand(
    string Username,
    string Password,
    string IpAddress
) : IRequest<ErrorOr<AuthLoginResponse>>;
