using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.RefreshToken;

public sealed class RefreshTokenHandler(
    IAuthService authService,
    IAuditService auditService
) : IRequestHandler<RefreshTokenCommand, ErrorOr<AuthLoginResponse>>
{
    public async Task<ErrorOr<AuthLoginResponse>> Handle(
        RefreshTokenCommand command,
        CancellationToken cancellationToken)
    {
        var result = await authService.RefreshTokenAsync(command.RefreshToken, command.IpAddress);

        if (result is null)
        {
            // Not logged again here: AuthService has already said why — unknown token, expired,
            // revoked, or an inactive user — and a second, vaguer line for the same attempt only
            // doubled the count. Twelve handsets retrying once each after an administrator revoked
            // their tokens read as twenty-four warnings.
            return Errors.Auth.InvalidRefreshToken;
        }

        var username = result.User?.Username ?? "Unknown";
        var role = result.User?.Role ?? "Unknown";
        var details = $"Session renewed for {username}; refresh token rotated from IP {command.IpAddress}.";

        try { await auditService.LogAsync(AuditActions.RefreshToken, username, role, "Session", username, details); } catch { }
        return result;
    }
}
