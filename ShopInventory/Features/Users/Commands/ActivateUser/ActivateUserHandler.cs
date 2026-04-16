using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Users.Commands.ActivateUser;

public sealed class ActivateUserHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<ActivateUserHandler> logger
) : IRequestHandler<ActivateUserCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ActivateUserCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.FindAsync(new object[] { command.Id }, cancellationToken);

        if (user is null)
        {
            return Errors.User.NotFound(command.Id);
        }

        user.IsActive = true;
        user.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {Username} activated by admin", user.Username);

        try { await auditService.LogAsync(AuditActions.ActivateUser, "User", command.Id.ToString(), $"User {user.Username} activated", true); } catch { }

        return Result.Success;
    }
}
