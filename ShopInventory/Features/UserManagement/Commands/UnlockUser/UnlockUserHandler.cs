using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UnlockUser;

public sealed class UnlockUserHandler(
    IHttpContextAccessor httpContextAccessor,
    ICallerAccountReader callerAccounts,
    IUserManagementService userManagementService,
    IAuditService auditService
) : IRequestHandler<UnlockUserCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UnlockUserCommand command,
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

        var result = await userManagementService.UnlockUserAsync(command.Id);
        if (!result.IsSuccess)
        {
            return Errors.UserManagement.NotFound(command.Id);
        }

        try { await auditService.LogAsync(AuditActions.UnlockUser, "User", command.Id.ToString(), $"User {command.Id} unlocked", true); } catch { }

        return Result.Success;
    }
}
