using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.LogoutVanSalesCustomer;

/// <summary>
/// Revokes the tokens that keep a device signed in.
/// </summary>
/// <remarks>
/// Always reports success, including when there was nothing to revoke. Signing out is the one thing
/// a worried customer will try after losing a phone, and an error at that moment reads as "it did
/// not work" whatever it actually meant. There is also nothing useful to report: a token that
/// cannot be found is, from the caller's position, already gone.
/// <para>
/// The account comes from the caller's token rather than the request, so this can only ever end the
/// caller's own sessions.
/// </para>
/// </remarks>
public sealed class LogoutVanSalesCustomerHandler(
    ApplicationDbContext context,
    ILogger<LogoutVanSalesCustomerHandler> logger)
    : IRequestHandler<LogoutVanSalesCustomerCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        LogoutVanSalesCustomerCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var query = context.VanSalesCustomerRefreshTokens
            .Where(t => t.VanSalesCustomerAccountId == command.AccountId && t.RevokedAt == null);

        if (!string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            var hash = VanSalesCustomerRefreshTokenValue.Hash(command.RefreshToken);
            query = query.Where(t => t.TokenHash == hash);
        }
        else if (!string.IsNullOrWhiteSpace(command.DeviceId))
        {
            query = query.Where(t => t.DeviceId == command.DeviceId);
        }

        // With neither a token nor a device supplied, every session ends. That is the right reading
        // of "sign me out" from someone who no longer has the device to name.
        var revoked = await query.ExecuteUpdateAsync(
            s => s.SetProperty(t => t.RevokedAt, now),
            cancellationToken);

        // The push registration goes with the session. Otherwise a handset the customer has signed
        // out of — or lost — keeps receiving that shop's order notifications.
        var devices = context.VanSalesCustomerDevices
            .Where(d => d.VanSalesCustomerAccountId == command.AccountId && !d.IsRevoked);

        if (!string.IsNullOrWhiteSpace(command.DeviceId))
        {
            devices = devices.Where(d => d.DeviceId == command.DeviceId);
        }

        await devices.ExecuteUpdateAsync(
            s => s.SetProperty(d => d.IsRevoked, true),
            cancellationToken);

        logger.LogInformation(
            "Signed out van sales customer account {AccountId}: {Revoked} token(s) revoked.",
            command.AccountId,
            revoked);

        return Result.Success;
    }
}
