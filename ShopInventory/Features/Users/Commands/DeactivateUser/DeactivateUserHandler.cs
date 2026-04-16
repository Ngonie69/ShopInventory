using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Users.Commands.DeactivateUser;

public sealed class DeactivateUserHandler(
    ApplicationDbContext context,
    IAuditService auditService,
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

        return Result.Success;
    }
}
