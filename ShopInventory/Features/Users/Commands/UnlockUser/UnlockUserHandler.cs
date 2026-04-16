using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Users.Commands.UnlockUser;

public sealed class UnlockUserHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<UnlockUserHandler> logger
) : IRequestHandler<UnlockUserCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UnlockUserCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.FindAsync(new object[] { command.Id }, cancellationToken);

        if (user is null)
        {
            return Errors.User.NotFound(command.Id);
        }

        user.LockoutEnd = null;
        if (command.Request?.ResetFailedAttempts ?? true)
        {
            user.FailedLoginAttempts = 0;
        }
        user.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {Username} unlocked by admin", user.Username);

        try { await auditService.LogAsync(AuditActions.UnlockUser, "User", command.Id.ToString(), $"User {user.Username} unlocked", true); } catch { }

        return Result.Success;
    }
}
