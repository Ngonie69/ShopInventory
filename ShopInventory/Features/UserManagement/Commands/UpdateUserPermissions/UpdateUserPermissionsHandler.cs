using System.Security.Claims;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UpdateUserPermissions;

public sealed class UpdateUserPermissionsHandler(
    ApplicationDbContext context,
    IHttpContextAccessor httpContextAccessor,
    IUserManagementService userManagementService,
    IAuditService auditService
) : IRequestHandler<UpdateUserPermissionsCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateUserPermissionsCommand command,
        CancellationToken cancellationToken)
    {
        var currentUserId = UserClaimReader.GetUserId(httpContextAccessor.HttpContext?.User);
        if (currentUserId is null)
        {
            return Errors.UserManagement.Unauthenticated;
        }

        var currentUser = await context.Users
            .AsNoTracking()
            .Where(user => user.Id == currentUserId.Value)
            .Select(user => new { user.Role, user.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (currentUser is null || !currentUser.IsActive)
        {
            return Errors.UserManagement.Unauthenticated;
        }

        if (string.Equals(currentUser.Role, ApplicationRoles.PodOperator, StringComparison.OrdinalIgnoreCase))
        {
            var targetUser = await context.Users
                .AsNoTracking()
                .Where(user => user.Id == command.Id)
                .Select(user => new { user.Role })
                .FirstOrDefaultAsync(cancellationToken);

            if (targetUser is null)
            {
                return Errors.UserManagement.NotFound(command.Id);
            }

            if (!string.Equals(targetUser.Role, ApplicationRoles.Driver, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.UserManagement.PodOperatorCanOnlyManageDrivers;
            }

            if (command.Request.Permissions.Count > 0)
            {
                return Errors.UserManagement.PodOperatorCannotAssignCustomPermissionsOnUpdate;
            }
        }

        var result = await userManagementService.UpdateUserPermissionsAsync(command.Id, command.Request);
        if (!result.IsSuccess)
        {
            return Errors.UserManagement.UpdateFailed(result.Message);
        }

        try { await auditService.LogAsync(AuditActions.UpdatePermissions, "User", command.Id.ToString(), $"Permissions updated for user {command.Id}", true); } catch { }

        return Result.Success;
    }
}
