using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.Login;

public sealed class LoginHandler(
    IAuthService authService,
    IAuditService auditService,
    ILogger<LoginHandler> logger
) : IRequestHandler<LoginCommand, ErrorOr<AuthLoginResponse>>
{
    public async Task<ErrorOr<AuthLoginResponse>> Handle(
        LoginCommand command,
        CancellationToken cancellationToken)
    {
        if (authService.IsLockedOut(command.IpAddress))
        {
            logger.LogWarning("Login attempt from locked out IP: {IpAddress}", command.IpAddress);
            return Errors.Auth.LockedOut;
        }

        var request = new AuthLoginRequest
        {
            Username = command.Username,
            Password = command.Password
        };

        var result = await authService.AuthenticateAsync(request, command.IpAddress);

        if (result is null)
        {
            try { await auditService.LogAsync(AuditActions.LoginFailed, command.Username, "Unknown", "User", null, $"Failed login attempt for {command.Username}", null, false, "Invalid username or password"); } catch { }
            return Errors.Auth.InvalidCredentials;
        }

        try { await auditService.LogAsync(AuditActions.Login, result.User?.Username ?? command.Username, result.User?.Role ?? "Unknown", "User", null, $"User {result.User?.Username ?? command.Username} logged in"); } catch { }
        return result;
    }
}
