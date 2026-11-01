using ErrorOr;
using MediatR;
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

        if (result.IsError)
        {
            try
            {
                await auditService.LogAsync(
                    AuditActions.LoginFailed, "Unknown", "Unknown", "User", null,
                    "Failed 2FA challenge", null, false, result.FirstError.Code);
            }
            catch { }
            return result.Errors;
        }

        var response = result.Value;
        try
        {
            await auditService.LogAsync(
                AuditActions.Login, response.User?.Username ?? "Unknown", response.User?.Role ?? "Unknown",
                "User", null, $"User {response.User?.Username} completed 2FA login");
        }
        catch { }

        return response;
    }
}
