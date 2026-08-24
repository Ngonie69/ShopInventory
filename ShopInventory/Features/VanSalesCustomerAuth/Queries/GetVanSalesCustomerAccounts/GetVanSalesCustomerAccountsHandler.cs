using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesCustomerAuth.Queries.GetVanSalesCustomerAccounts;

/// <summary>
/// Lists customer sign-ins for the operator screen.
/// </summary>
/// <remarks>
/// The lockout is projected as a boolean computed against the clock at read time rather than as the
/// stored <c>LockedUntil</c>. An operator asking "why can this shop not sign in?" wants the answer
/// now, and a timestamp in the past reads as locked to anyone scanning the column.
/// </remarks>
public sealed class GetVanSalesCustomerAccountsHandler(ApplicationDbContext context)
    : IRequestHandler<GetVanSalesCustomerAccountsQuery, ErrorOr<List<VanSalesCustomerAccountResult>>>
{
    public async Task<ErrorOr<List<VanSalesCustomerAccountResult>>> Handle(
        GetVanSalesCustomerAccountsQuery query,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var accounts = context.VanSalesCustomerAccounts
            .AsNoTracking()
            .Where(a => a.RouteCustomer != null);

        if (query.RouteCustomerId is { } routeCustomerId)
        {
            accounts = accounts.Where(a => a.RouteCustomerId == routeCustomerId);
        }

        if (!query.IncludeInactive)
        {
            accounts = accounts.Where(a => a.IsActive);
        }

        var results = await accounts
            .OrderBy(a => a.RouteCustomer!.Name)
            .ThenBy(a => a.PhoneE164)
            .Select(a => new VanSalesCustomerAccountResult(
                a.Id,
                a.RouteCustomerId,
                a.RouteCustomer!.Code,
                a.RouteCustomer.Name,
                a.PhoneE164,
                a.DisplayName,
                a.IsActive,
                a.LockedUntil != null && a.LockedUntil > now,
                a.LastLoginAt,
                a.CreatedAt))
            .ToListAsync(cancellationToken);

        return results;
    }
}
