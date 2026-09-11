using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.ResetTwoFactor;

public sealed class ResetTwoFactorHandler(
    IHttpContextAccessor httpContextAccessor,
    ICallerAccountReader callerAccounts,
    IUserManagementService userManagementService,
    IAuditService auditService
) : IRequestHandler<ResetTwoFactorCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ResetTwoFactorCommand command,
        CancellationToken cancellationToken)
    {
        var caller = await callerAccounts.ReadAsync(httpContextAccessor.HttpContext?.User, cancellationToken);
        if (caller.IsError)
        {
            return caller.Errors;
        }

        if (caller.Value.IsInRole(ApplicationRoles.PodOperator))
        {
            var targetUser = await userManagementService.GetUserByIdAsync(command.Id);
            if (targetUser is null)
            {
                return Errors.UserManagement.NotFound(command.Id);
            }

            if (!string.Equals(targetUser.Role, ApplicationRoles.Driver, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.UserManagement.PodOperatorCanOnlyManageDrivers;
            }
        }

        var result = await userManagementService.ResetTwoFactorAsync(command.Id);
        if (!result.IsSuccess)
        {
            return Errors.UserManagement.NotFound(command.Id);
        }

        try { await auditService.LogAsync(AuditActions.ResetTwoFactor, "User", command.Id.ToString(), $"2FA reset for user {command.Id}", true); } catch { }

        return Result.Success;
    }
}
