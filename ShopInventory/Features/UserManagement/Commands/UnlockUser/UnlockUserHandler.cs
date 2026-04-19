using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UnlockUser;

public sealed class UnlockUserHandler(
    IUserManagementService userManagementService,
    IAuditService auditService
) : IRequestHandler<UnlockUserCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UnlockUserCommand command,
        CancellationToken cancellationToken)
    {
        var result = await userManagementService.UnlockUserAsync(command.Id);
        if (!result.IsSuccess)
        {
            return Errors.UserManagement.NotFound(command.Id);
        }

        try { await auditService.LogAsync(AuditActions.UnlockUser, "User", command.Id.ToString(), $"User {command.Id} unlocked", true); } catch { }

        return Result.Success;
    }
}
