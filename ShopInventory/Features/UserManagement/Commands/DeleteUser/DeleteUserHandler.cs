using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.DeleteUser;

public sealed class DeleteUserHandler(
    IUserManagementService userManagementService,
    IAuditService auditService,
    ILogger<DeleteUserHandler> logger
) : IRequestHandler<DeleteUserCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        DeleteUserCommand command,
        CancellationToken cancellationToken)
    {
        var result = await userManagementService.DeleteUserAsync(command.Id);
        if (!result.IsSuccess)
        {
            return Errors.UserManagement.NotFound(command.Id);
        }

        try { await auditService.LogAsync(AuditActions.DeleteUser, "User", command.Id.ToString(), $"User {command.Id} deleted", true); } catch { }

        return Result.Success;
    }
}
