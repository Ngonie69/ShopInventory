using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Commands.CompleteTwoFactorLogin;

public sealed record CompleteTwoFactorLoginCommand(
    string ChallengeToken,
    string Code,
    bool IsBackupCode,
    string IpAddress
) : IRequest<ErrorOr<AuthLoginResponse>>;
