using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Commands.CompletePasskeyLogin;

public sealed record CompletePasskeyLoginCommand(
    string SessionToken,
    string CredentialJson,
    string Origin,
    string RpId,
    string IpAddress) : IRequest<ErrorOr<AuthLoginResponse>>;