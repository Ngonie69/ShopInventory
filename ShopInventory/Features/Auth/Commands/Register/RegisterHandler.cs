using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.Register;

public sealed class RegisterHandler(
    IAuthService authService,
    IAuditService auditService,
    ILogger<RegisterHandler> logger
) : IRequestHandler<RegisterCommand, ErrorOr<UserInfo>>
{
    public async Task<ErrorOr<UserInfo>> Handle(
        RegisterCommand command,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Admin {Admin} attempting to register new user: {Username}",
            command.AdminUsername, command.Username);

        if (!ApplicationRoles.IsAssignableRole(command.Role))
        {
            return Errors.Auth.InvalidRole(command.Role);
        }

        var user = await authService.RegisterUserAsync(
            command.Username,
            command.Email ?? string.Empty,
            command.Password,
            command.Role.Trim());

        if (user is null)
        {
            return Errors.Auth.DuplicateUser;
        }

        logger.LogInformation("Admin {Admin} successfully registered new user: {Username} with role {Role}",
            command.AdminUsername, user.Username, user.Role);

        try { await auditService.LogAsync(AuditActions.RegisterUser, "User", user.Username, $"Registered user {user.Username} with role {user.Role}", true); } catch { }

        return new UserInfo
        {
            Username = user.Username,
            Role = user.Role,
            Email = user.Email
        };
    }
}
