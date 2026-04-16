using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UpdateUser;

public sealed class UpdateUserHandler(
    IUserManagementService userManagementService,
    IAuditService auditService,
    ILogger<UpdateUserHandler> logger
) : IRequestHandler<UpdateUserCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateUserCommand command,
        CancellationToken cancellationToken)
    {
        var result = await userManagementService.UpdateUserAsync(command.Id, command.Request);
        if (!result.IsSuccess)
        {
            return Errors.UserManagement.UpdateFailed(result.Message);
        }

        try { await auditService.LogAsync(AuditActions.UpdateUser, "User", command.Id.ToString(), $"User {command.Id} updated", true); } catch { }

        return Result.Success;
    }
}
