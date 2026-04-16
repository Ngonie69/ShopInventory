using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Users.Commands.DeleteUser;

public sealed class DeleteUserHandler(
    ApplicationDbContext context,
    IAuditService auditService,
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

        context.Users.Remove(user);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {Username} deleted by admin", user.Username);

        try { await auditService.LogAsync(AuditActions.DeleteUser, "User", command.Id.ToString(), $"User {user.Username} deleted", true); } catch { }

        return Result.Deleted;
    }
}
