using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Users.Commands.DeleteUser;

public sealed class DeleteUserHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<DeleteUserHandler> logger
) : IRequestHandler<DeleteUserCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteUserCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.FindAsync(new object[] { command.Id }, cancellationToken);

        if (user is null)
        {
            return Errors.User.NotFound(command.Id);
        }

        if (user.Role == "Admin")
        {
            var adminCount = await context.Users.CountAsync(u => u.Role == "Admin", cancellationToken);
            if (adminCount <= 1)
            {
                return Errors.User.LastAdmin;
            }
        }

        // Captured before the entity is detached — after SaveChanges the tracked instance is gone
        // and reading it back to build the message would be a read of a deleted row.
        var deletedUserId = user.Id;
        var deletedUsername = user.Username;
        var deletedRole = user.Role;

        context.Users.Remove(user);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {Username} deleted by admin", deletedUsername);

        try { await auditService.LogAsync(AuditActions.DeleteUser, "User", command.Id.ToString(), $"User {deletedUsername} deleted", true); } catch { }

        try
        {
            await notificationService.CreateNotificationAsync(
                ModuleNotificationFactory.CreateBroadcastNotification(
                    $"User Account Deleted: {deletedUsername}",
                    $"Account {deletedUsername} ({deletedRole}) was deleted.",
                    "Warning",
                    "Security",
                    "User",
                    deletedUserId.ToString(),
                    "/user-management",
                    new Dictionary<string, string>
                    {
                        ["userId"] = deletedUserId.ToString(),
                        ["username"] = deletedUsername,
                        ["role"] = deletedRole
                    }),
                cancellationToken);
        }
        catch (Exception notificationException)
        {
            logger.LogWarning(
                notificationException,
                "Failed to publish user deletion notification for {Username}",
                deletedUsername);
        }

        return Result.Deleted;
    }
}
