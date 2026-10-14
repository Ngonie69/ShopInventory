using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Features.Password;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Users.Commands.RequirePasswordChange;

public sealed class RequirePasswordChangeHandler(
    ApplicationDbContext context,
    UserPasswords passwords,
    IAuditService auditService,
    ILogger<RequirePasswordChangeHandler> logger
) : IRequestHandler<RequirePasswordChangeCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RequirePasswordChangeCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.FindAsync(new object[] { command.Id }, cancellationToken);

        if (user is null)
        {
            return Errors.User.NotFound(command.Id);
        }

        await passwords.RequireChangeAsync(user, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Password change required for user {Username} by admin", user.Username);

        try { await auditService.LogAsync(AuditActions.RequirePasswordChange, "User", command.Id.ToString(), $"Password change required for user {user.Username}; sessions ended", true); } catch { }

        return Result.Success;
    }
}
