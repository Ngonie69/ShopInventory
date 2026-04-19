using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.CreateUser;

public sealed class CreateUserHandler(
    IUserManagementService userManagementService,
    IAuditService auditService
) : IRequestHandler<CreateUserCommand, ErrorOr<UserDetailDto>>
{
    public async Task<ErrorOr<UserDetailDto>> Handle(
        CreateUserCommand command,
        CancellationToken cancellationToken)
    {
        var result = await userManagementService.CreateUserAsync(command.Request);
        if (!result.IsSuccess)
        {
            return Errors.UserManagement.CreationFailed(result.Message);
        }

        try { await auditService.LogAsync(AuditActions.CreateUser, "User", result.Data!.Id.ToString(), $"User {command.Request.Username} created with role {command.Request.Role}", true); } catch { }

        return result.Data!;
    }
}
