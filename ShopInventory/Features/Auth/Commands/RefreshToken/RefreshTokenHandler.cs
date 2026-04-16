using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.RefreshToken;

public sealed class RefreshTokenHandler(
    IAuthService authService,
    IAuditService auditService,
    ILogger<RefreshTokenHandler> logger
) : IRequestHandler<RefreshTokenCommand, ErrorOr<AuthLoginResponse>>
{
    public async Task<ErrorOr<AuthLoginResponse>> Handle(
        RefreshTokenCommand command,
        CancellationToken cancellationToken)
    {
        var result = await authService.RefreshTokenAsync(command.RefreshToken, command.IpAddress);

        if (result is null)
        {
            logger.LogWarning("Invalid refresh token attempt from IP: {IpAddress}", command.IpAddress);
            return Errors.Auth.InvalidRefreshToken;
        }

        try { await auditService.LogAsync(AuditActions.RefreshToken, "User"); } catch { }
        return result;
    }
}
