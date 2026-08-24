using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.DeactivateVanSalesCustomerAccount;

/// <summary>
/// Withdraws a customer's access to the ordering app.
/// </summary>
/// <remarks>
/// Clearing <c>IsActive</c> alone would stop the next sign-in and leave the current one running:
/// the access token is already minted and valid until it expires, and the refresh token would keep
/// renewing it for months. The reason this is used — a handset lost, a shop changing hands, a
/// falling-out — is exactly the case where "they stay signed in for now" is the wrong answer, so
/// the refresh tokens are revoked in the same breath.
/// <para>
/// The access token already issued cannot be recalled; it is short-lived for this reason, and
/// nothing can refresh it once the tokens below are gone.
/// </para>
/// The row itself is kept. Orders point at the account that placed them, and deleting it would
/// leave that history unattributable.
/// </remarks>
public sealed class DeactivateVanSalesCustomerAccountHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<DeactivateVanSalesCustomerAccountHandler> logger)
    : IRequestHandler<DeactivateVanSalesCustomerAccountCommand, ErrorOr<VanSalesCustomerAccountResult>>
{
    public async Task<ErrorOr<VanSalesCustomerAccountResult>> Handle(
        DeactivateVanSalesCustomerAccountCommand command,
        CancellationToken cancellationToken)
    {
        var account = await context.VanSalesCustomerAccounts
            .Include(a => a.RouteCustomer)
            .FirstOrDefaultAsync(a => a.Id == command.AccountId, cancellationToken);

        if (account is null || account.RouteCustomer is null)
        {
            return Errors.VanSalesCustomerAuth.AccountNotFound(command.AccountId);
        }

        var now = DateTime.UtcNow;

        account.IsActive = false;
        account.UpdatedAt = now;

        var revoked = await context.VanSalesCustomerRefreshTokens
            .Where(t => t.VanSalesCustomerAccountId == account.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.DeactivateVanSalesCustomerAccount,
                "VanSalesCustomerAccount",
                account.Id.ToString(),
                $"App sign-in for route customer {account.RouteCustomer.Code} withdrawn; {revoked} session(s) ended.",
                true);
        }
        catch
        {
            // Auditing must not cost the operator the change they just made.
        }

        logger.LogInformation(
            "Deactivated van sales customer sign-in {AccountId} and revoked {Revoked} session(s).",
            account.Id,
            revoked);

        return new VanSalesCustomerAccountResult(
            account.Id,
            account.RouteCustomerId,
            account.RouteCustomer.Code,
            account.RouteCustomer.Name,
            account.PhoneE164,
            account.DisplayName,
            account.IsActive,
            account.IsLockedOut,
            account.LastLoginAt,
            account.CreatedAt);
    }
}
