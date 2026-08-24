using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <inheritdoc />
public sealed class VanSalesCustomerSessionIssuer(
    ApplicationDbContext context,
    IVanSalesCustomerTokenIssuer tokenIssuer,
    IOptions<VanSalesCustomerAuthSettings> authSettings,
    ILogger<VanSalesCustomerSessionIssuer> logger) : IVanSalesCustomerSessionIssuer
{
    private readonly VanSalesCustomerAuthSettings _settings = authSettings.Value;

    public async Task<ErrorOr<VanSalesCustomerSessionResult>> IssueAsync(
        int accountId,
        string? deviceId,
        string? deviceName,
        string? ipAddress,
        string? replacesTokenHash,
        CancellationToken cancellationToken)
    {
        var account = await context.VanSalesCustomerAccounts
            .Include(a => a.RouteCustomer)
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);

        // Re-checked here rather than trusted from the caller: this is the last gate before a token
        // exists, and a refresh arriving minutes after an operator switched the account off must not
        // be honoured because the caller checked before that happened.
        if (account is null || !account.IsActive || account.RouteCustomer is null)
        {
            return Errors.VanSalesCustomerAuth.AccountInactive;
        }

        if (!account.RouteCustomer.IsActive)
        {
            logger.LogInformation(
                "Refused a van sales customer session for account {AccountId}: route customer {Code} is inactive.",
                account.Id,
                account.RouteCustomer.Code);
            return Errors.VanSalesCustomerAuth.AccountInactive;
        }

        var now = DateTime.UtcNow;
        var (accessToken, expiresAt) = tokenIssuer.IssueAccessToken(account, account.RouteCustomer.Code);

        var refreshValue = VanSalesCustomerRefreshTokenValue.Generate();
        var refreshHash = VanSalesCustomerRefreshTokenValue.Hash(refreshValue);

        if (!string.IsNullOrEmpty(replacesTokenHash))
        {
            // Rotation: point the outgoing row at its successor. That link is what later tells a
            // rotated token apart from one an operator revoked, which must never be honoured again.
            await context.VanSalesCustomerRefreshTokens
                .Where(t => t.TokenHash == replacesTokenHash && t.RevokedAt == null)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(t => t.RevokedAt, now)
                        .SetProperty(t => t.ReplacedByTokenHash, refreshHash),
                    cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(deviceId))
        {
            // A fresh sign-in on a device supersedes that device's previous session. Without this a
            // handset that signs in repeatedly leaves a trail of live tokens, each one still able to
            // mint access tokens long after the person stopped using it.
            await context.VanSalesCustomerRefreshTokens
                .Where(t => t.VanSalesCustomerAccountId == account.Id
                            && t.DeviceId == deviceId
                            && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
        }

        context.VanSalesCustomerRefreshTokens.Add(new VanSalesCustomerRefreshTokenEntity
        {
            Id = Guid.NewGuid(),
            VanSalesCustomerAccountId = account.Id,
            TokenHash = refreshHash,
            DeviceId = deviceId,
            DeviceName = deviceName,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_settings.RefreshTokenExpirationDays),
            CreatedByIp = ipAddress
        });

        account.LastLoginAt = now;
        account.FailedOtpCount = 0;
        account.LockedUntil = null;
        account.UpdatedAt = now;

        await context.SaveChangesAsync(cancellationToken);

        return new VanSalesCustomerSessionResult(
            accessToken,
            refreshValue,
            expiresAt,
            new VanSalesCustomerSummary(
                account.Id,
                account.RouteCustomer.Code,
                account.RouteCustomer.Name,
                account.DisplayName,
                account.RouteCustomer.Phone,
                account.RouteCustomer.Address));
    }
}
