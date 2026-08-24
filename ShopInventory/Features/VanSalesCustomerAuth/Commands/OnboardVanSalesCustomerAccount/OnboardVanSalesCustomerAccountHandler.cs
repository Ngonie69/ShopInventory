using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.OnboardVanSalesCustomerAccount;

/// <summary>
/// Creates a customer's sign-in, or reinstates and re-points one that already exists.
/// </summary>
/// <remarks>
/// Onboarding the same shop twice is the normal case, not the exception: a shopkeeper changes
/// handset, loses a number, or the rep repeats the visit. So a phone that already has an account is
/// updated rather than rejected — but only when it still belongs to the same customer. The same
/// number arriving for a <em>different</em> customer is refused, because honouring it would silently
/// move the first customer's orders onto someone else's phone.
/// </remarks>
public sealed class OnboardVanSalesCustomerAccountHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    IOptions<VanSalesCustomerAuthSettings> authSettings,
    ILogger<OnboardVanSalesCustomerAccountHandler> logger)
    : IRequestHandler<OnboardVanSalesCustomerAccountCommand, ErrorOr<VanSalesCustomerAccountResult>>
{
    private readonly VanSalesCustomerAuthSettings _settings = authSettings.Value;

    public async Task<ErrorOr<VanSalesCustomerAccountResult>> Handle(
        OnboardVanSalesCustomerAccountCommand command,
        CancellationToken cancellationToken)
    {
        if (!VanSalesCustomerPhone.TryNormalise(
                command.PhoneNumber,
                _settings.DefaultCountryCode,
                out var phone))
        {
            return Errors.VanSalesCustomerAuth.InvalidPhoneNumber;
        }

        var routeCustomer = await context.RouteCustomers
            .AsNoTracking()
            .Where(c => c.Id == command.RouteCustomerId)
            .Select(c => new { c.Id, c.Code, c.Name, c.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (routeCustomer is null)
        {
            return Errors.VanSalesCustomerAuth.RouteCustomerNotFound(command.RouteCustomerId);
        }

        if (!routeCustomer.IsActive)
        {
            return Errors.VanSalesCustomerAuth.RouteCustomerInactive(routeCustomer.Code);
        }

        var now = DateTime.UtcNow;

        var existing = await context.VanSalesCustomerAccounts
            .FirstOrDefaultAsync(a => a.PhoneE164 == phone, cancellationToken);

        if (existing is not null && existing.RouteCustomerId != routeCustomer.Id)
        {
            logger.LogWarning(
                "Refused to move van sales customer sign-in {AccountId} from route customer {From} to {To}.",
                existing.Id,
                existing.RouteCustomerId,
                routeCustomer.Id);

            return Errors.VanSalesCustomerAuth.PhoneAlreadyInUse(VanSalesCustomerPhone.Mask(phone));
        }

        var account = existing;

        if (account is null)
        {
            account = new VanSalesCustomerAccountEntity
            {
                RouteCustomerId = routeCustomer.Id,
                PhoneE164 = phone,
                DisplayName = command.DisplayName,
                IsActive = true,
                CreatedByUserId = command.CreatedByUserId,
                CreatedAt = now
            };

            context.VanSalesCustomerAccounts.Add(account);
        }
        else
        {
            account.DisplayName = command.DisplayName ?? account.DisplayName;
            account.IsActive = true;

            // Re-onboarding clears a lockout. The rep is standing in the shop confirming who this
            // is, which is a stronger check than the one that locked it.
            account.FailedOtpCount = 0;
            account.LockedUntil = null;
            account.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.CreateVanSalesCustomerAccount,
                "VanSalesCustomerAccount",
                account.Id.ToString(),
                $"App sign-in for route customer {routeCustomer.Code} set to {VanSalesCustomerPhone.Mask(phone)}.",
                true);
        }
        catch
        {
            // Auditing must not cost the operator the change they just made; the surrounding
            // services treat it the same way.
        }

        logger.LogInformation(
            "Onboarded van sales customer sign-in {AccountId} for route customer {Code}.",
            account.Id,
            routeCustomer.Code);

        return new VanSalesCustomerAccountResult(
            account.Id,
            routeCustomer.Id,
            routeCustomer.Code,
            routeCustomer.Name,
            account.PhoneE164,
            account.DisplayName,
            account.IsActive,
            account.IsLockedOut,
            account.LastLoginAt,
            account.CreatedAt);
    }
}
