using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UpdateUserPermissions;

public sealed class UpdateUserPermissionsHandler(
    IUserManagementService userManagementService,
    IAuditService auditService
) : IRequestHandler<UpdateUserPermissionsCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateUserPermissionsCommand command,
        CancellationToken cancellationToken)
    {
        var result = await userManagementService.UpdateUserPermissionsAsync(command.Id, command.Request);
        if (!result.IsSuccess)
        {
            return Errors.UserManagement.UpdateFailed(result.Message);
        }

        try { await auditService.LogAsync(AuditActions.UpdatePermissions, "User", command.Id.ToString(), $"Permissions updated for user {command.Id}", true); } catch { }

        return Result.Success;
    }
}
