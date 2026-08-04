using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Users.Commands.DeactivateUser;

public sealed class DeactivateUserHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<DeactivateUserHandler> logger
) : IRequestHandler<DeactivateUserCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        DeactivateUserCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.FindAsync(new object[] { command.Id }, cancellationToken);

        if (user is null)
        {
            return Errors.User.NotFound(command.Id);
        }

        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {Username} deactivated by admin", user.Username);

        try { await auditService.LogAsync(AuditActions.DeactivateUser, "User", command.Id.ToString(), $"User {user.Username} deactivated", true); } catch { }

        try
        {
            await notificationService.CreateNotificationAsync(
                ModuleNotificationFactory.CreateBroadcastNotification(
                    $"User Account Deactivated: {user.Username}",
                    $"Account {user.Username} ({user.Role}) was deactivated and can no longer sign in.",
                    "Warning",
                    "Security",
                    "User",
                    user.Id.ToString(),
                    "/user-management",
                    new Dictionary<string, string>
                    {
                        ["userId"] = user.Id.ToString(),
                        ["username"] = user.Username,
                        ["role"] = user.Role,
                        ["isActive"] = "false"
                    }),
                cancellationToken);
        }
        catch (Exception notificationException)
        {
            logger.LogWarning(
                notificationException,
                "Failed to publish user deactivation notification for {Username}",
                user.Username);
        }

        return Result.Success;
    }
}
