using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;
using BC = BCrypt.Net.BCrypt;

namespace ShopInventory.Features.Users.Commands.AdminChangePassword;

public sealed class AdminChangePasswordHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<AdminChangePasswordHandler> logger
) : IRequestHandler<AdminChangePasswordCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        AdminChangePasswordCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.Request.NewPassword) || command.Request.NewPassword.Length < 8)
        {
            return Errors.User.UpdateFailed("Password must be at least 8 characters");
        }

        var user = await context.Users.FindAsync(new object[] { command.Id }, cancellationToken);

        if (user is null)
        {
            return Errors.User.NotFound(command.Id);
        }

        user.PasswordHash = BC.HashPassword(command.Request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Password changed for user {Username} by admin", user.Username);

        try { await auditService.LogAsync(AuditActions.ChangePassword, "User", command.Id.ToString(), $"Password changed for user {user.Username}", true); } catch { }

        return Result.Success;
    }
}
