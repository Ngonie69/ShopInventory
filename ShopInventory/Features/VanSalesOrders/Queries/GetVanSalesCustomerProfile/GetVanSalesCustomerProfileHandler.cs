using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerProfile;

/// <summary>
/// Assembles the signed-in shop's profile and its next delivery window.
/// </summary>
/// <remarks>
/// The route is <em>derived</em>, not stored on the customer. A route customer belongs to a van
/// through <c>AssignedBusinessPartnerCode</c> — as <c>VanSalesRouteName</c> puts it, the assigned
/// business partner is the rep's route — and that van's user account carries the
/// <c>RouteId</c>. Copying the route onto the customer as well would give the same question two
/// answers, and they would part company the first time a shop was moved between vans.
/// <para>
/// Nothing here fails when the route cannot be resolved. A van account may not have a route
/// recorded, and a shop whose van is between accounts still needs to order; the route is a label on
/// this screen, not a permission.
/// </para>
/// </remarks>
public sealed class GetVanSalesCustomerProfileHandler(
    ApplicationDbContext context,
    IVanSalesOrderingPolicy orderingPolicy)
    : IRequestHandler<GetVanSalesCustomerProfileQuery, ErrorOr<VanSalesCustomerProfileResult>>
{
    public async Task<ErrorOr<VanSalesCustomerProfileResult>> Handle(
        GetVanSalesCustomerProfileQuery query,
        CancellationToken cancellationToken)
    {
        var account = await context.VanSalesCustomerAccounts
            .AsNoTracking()
            .Where(a => a.Id == query.AccountId && a.IsActive && a.RouteCustomer != null)
            .Select(a => new
            {
                a.Id,
                a.DisplayName,
                a.PhoneE164,
                RouteCustomerId = a.RouteCustomerId,
                Code = a.RouteCustomer!.Code,
                Name = a.RouteCustomer.Name,
                a.RouteCustomer.Address,
                CustomerPhone = a.RouteCustomer.Phone,
                a.RouteCustomer.AssignedBusinessPartnerCode,
                CustomerActive = a.RouteCustomer.IsActive
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (account is null || !account.CustomerActive)
        {
            return Errors.VanSalesCustomerAuth.AccountInactive;
        }

        var visitDays = await context.RouteCustomerVisitDays
            .AsNoTracking()
            .Where(d => d.RouteCustomerId == account.RouteCustomerId)
            .OrderBy(d => d.DayOfWeek)
            .Select(d => d.DayOfWeek)
            .ToListAsync(cancellationToken);

        // The van serving this shop, and through it the route. FirstOrDefault rather than Single:
        // nothing constrains a business partner to one account, and a duplicate must not turn a
        // profile screen into an error.
        var route = await context.Users
            .AsNoTracking()
            .Where(u => u.AssignedBusinessPartnerCode == account.AssignedBusinessPartnerCode
                        && u.RouteId != null
                        && u.Route != null)
            .Select(u => new
            {
                u.Route!.Code,
                u.Route.Name,
                u.Route.Territory
            })
            .FirstOrDefaultAsync(cancellationToken);

        var rules = await orderingPolicy.GetRulesAsync(cancellationToken);
        var window = VanSalesVisitSchedule.NextOpenVisit(
            DateTime.UtcNow,
            visitDays,
            rules.CutOffHoursBeforeVisitDay);

        return new VanSalesCustomerProfileResult(
            account.Id,
            account.Code,
            account.Name,
            account.DisplayName,
            account.CustomerPhone ?? account.PhoneE164,
            account.Address,
            route?.Code,
            route?.Name,
            route?.Territory,
            visitDays,
            window.NextVisitDate,
            window.OrdersCloseAtUtc,
            window.HasSchedule,
            window.IsOrderingOpen);
    }
}
