using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.ResetTwoFactor;

public sealed class ResetTwoFactorHandler(
    IUserManagementService userManagementService,
    IAuditService auditService
) : IRequestHandler<ResetTwoFactorCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ResetTwoFactorCommand command,
        CancellationToken cancellationToken)
    {
        var result = await userManagementService.ResetTwoFactorAsync(command.Id);
        if (!result.IsSuccess)
        {
            return Errors.UserManagement.NotFound(command.Id);
        }

        try { await auditService.LogAsync(AuditActions.ResetTwoFactor, "User", command.Id.ToString(), $"2FA reset for user {command.Id}", true); } catch { }

        return Result.Success;
    }
}
