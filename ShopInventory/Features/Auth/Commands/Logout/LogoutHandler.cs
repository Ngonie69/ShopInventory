using ErrorOr;
using MediatR;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.Logout;

public sealed class LogoutHandler(
    IAuthService authService,
    IAuditService auditService,
    ILogger<LogoutHandler> logger
) : IRequestHandler<LogoutCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        LogoutCommand command,
        CancellationToken cancellationToken)
    {
        await authService.RevokeTokenAsync(command.RefreshToken, command.IpAddress);
        try { await auditService.LogAsync(AuditActions.Logout, "User"); } catch { }

        logger.LogInformation("User logged out from IP: {IpAddress}", command.IpAddress);
        return Result.Deleted;
    }
}
