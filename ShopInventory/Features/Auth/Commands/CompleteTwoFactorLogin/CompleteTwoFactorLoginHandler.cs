using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.CompleteTwoFactorLogin;

public sealed class CompleteTwoFactorLoginHandler(
    IAuthService authService,
    IAuditService auditService
) : IRequestHandler<CompleteTwoFactorLoginCommand, ErrorOr<AuthLoginResponse>>
{
    public async Task<ErrorOr<AuthLoginResponse>> Handle(
        CompleteTwoFactorLoginCommand command,
        CancellationToken cancellationToken)
    {
        var result = await authService.CompleteTwoFactorLoginAsync(
            command.ChallengeToken, command.Code, command.IsBackupCode, command.IpAddress, cancellationToken);

        if (result is null)
        {
            try
            {
                await auditService.LogAsync(
                    AuditActions.LoginFailed, "Unknown", "Unknown", "User", null,
                    "Failed 2FA challenge", null, false, "Invalid or expired 2FA code");
            }
            catch { }
            return Errors.Auth.InvalidCredentials;
        }

        try
        {
            await auditService.LogAsync(
                AuditActions.Login, result.User?.Username ?? "Unknown", result.User?.Role ?? "Unknown",
                "User", null, $"User {result.User?.Username} completed 2FA login");
        }
        catch { }

        return result;
    }
}
