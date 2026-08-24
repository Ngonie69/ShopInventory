using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.RefreshVanSalesCustomerSession;

/// <summary>
/// Rotates a customer's refresh token, and treats a second use of one as the theft it probably is.
/// </summary>
/// <remarks>
/// Rotation on its own only limits how long a stolen token is useful. What makes it detect theft is
/// what happens when a token that has already been rotated comes back: either the legitimate app is
/// retrying, or someone else is replaying a copy — and there is no way to tell which. So the whole
/// device's chain is revoked and both parties are made to sign in again. A customer re-entering a
/// code is a small cost; leaving an attacker holding a working session is not.
/// <para>
/// No grace window here, unlike the staff flow. Staff clients fire concurrent refreshes from a
/// browser with several requests in flight; this app refreshes from one place, serially, so a
/// repeat is a signal rather than a race.
/// </para>
/// </remarks>
public sealed class RefreshVanSalesCustomerSessionHandler(
    ApplicationDbContext context,
    IVanSalesCustomerSessionIssuer sessionIssuer,
    ILogger<RefreshVanSalesCustomerSessionHandler> logger)
    : IRequestHandler<RefreshVanSalesCustomerSessionCommand, ErrorOr<VanSalesCustomerSessionResult>>
{
    public async Task<ErrorOr<VanSalesCustomerSessionResult>> Handle(
        RefreshVanSalesCustomerSessionCommand command,
        CancellationToken cancellationToken)
    {
        var hash = VanSalesCustomerRefreshTokenValue.Hash(command.RefreshToken!);

        var token = await context.VanSalesCustomerRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null)
        {
            return Errors.VanSalesCustomerAuth.SessionExpired;
        }

        if (token.IsRevoked)
        {
            // Presented after it was rotated or revoked. Cut the whole device off: if this is a
            // replay, the copy the attacker holds dies with it.
            await RevokeDeviceChainAsync(token.VanSalesCustomerAccountId, token.DeviceId, cancellationToken);

            logger.LogWarning(
                "A revoked van sales customer refresh token was presented for account {AccountId}, device {DeviceId}. Revoked that device's remaining tokens.",
                token.VanSalesCustomerAccountId,
                token.DeviceId ?? "(unknown)");

            return Errors.VanSalesCustomerAuth.SessionExpired;
        }

        if (token.IsExpired)
        {
            return Errors.VanSalesCustomerAuth.SessionExpired;
        }

        return await sessionIssuer.IssueAsync(
            token.VanSalesCustomerAccountId,
            command.DeviceId ?? token.DeviceId,
            command.DeviceName ?? token.DeviceName,
            command.RequestedFromIp,
            replacesTokenHash: hash,
            cancellationToken);
    }

    private async Task RevokeDeviceChainAsync(
        int accountId,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var query = context.VanSalesCustomerRefreshTokens
            .Where(t => t.VanSalesCustomerAccountId == accountId && t.RevokedAt == null);

        // Scoped to the device when we know it, so one compromised handset does not sign the
        // customer out of the phone they are standing at. With no device id there is nothing to
        // scope by, and the safe reading of an unattributable replay is to end every session.
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            query = query.Where(t => t.DeviceId == deviceId);
        }

        await query.ExecuteUpdateAsync(
            s => s.SetProperty(t => t.RevokedAt, now),
            cancellationToken);
    }
}
