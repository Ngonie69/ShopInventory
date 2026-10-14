using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Password;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Users.Commands.AdminChangePassword;

public sealed class AdminChangePasswordHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    UserPasswords passwords,
    ILogger<AdminChangePasswordHandler> logger
) : IRequestHandler<AdminChangePasswordCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        AdminChangePasswordCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.FindAsync(new object[] { command.Id }, cancellationToken);

        if (user is null)
        {
            return Errors.User.NotFound(command.Id);
        }

        var brokenRules = await passwords.CheckForChangeAsync(user, command.Request.NewPassword, cancellationToken);
        if (brokenRules.Count > 0)
        {
            return Errors.Password.PolicyNotMet(brokenRules);
        }

        await passwords.ChangeAsync(
            user,
            command.Request.NewPassword,
            command.ChosenByOwner,
            command.ChosenByOwner ? "Password changed" : "Password reset by administrator",
            cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Password changed for user {Username} by {Actor}",
            user.Username,
            command.ChosenByOwner ? "its owner" : "an administrator");

        try { await auditService.LogAsync(AuditActions.ChangePassword, "User", command.Id.ToString(), $"Password changed for user {user.Username}", true); } catch { }

        return Result.Success;
    }
}
