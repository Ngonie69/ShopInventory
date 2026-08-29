using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.VanSalesCustomerAccounts.Queries.GetVanSalesCustomerAccounts;

/// <summary>
/// Loads the sign-ins and the route customers together.
/// </summary>
/// <remarks>
/// Two calls behind one query because the screen cannot usefully render half of it: the list shows
/// which shops already have access, and the form's picker shows which ones could. Fetching them
/// separately from the page would make the picker briefly empty on a slow line, which reads as "no
/// shops to choose from" at the moment an operator is deciding whether this is the right screen.
/// <para>
/// Route customers are fetched active-only. A sign-in for a shop that has been closed is not
/// something to offer, and the API refuses it anyway — offering it would only move the refusal to
/// after the operator has typed the number.
/// </para>
/// </remarks>
public sealed class GetVanSalesCustomerAccountsHandler(
    IVanSalesCustomerAccountService accountService,
    IRouteCustomerService routeCustomerService,
    ILogger<GetVanSalesCustomerAccountsHandler> logger
) : IRequestHandler<GetVanSalesCustomerAccountsQuery, ErrorOr<VanSalesCustomerAccountsViewModel>>
{
    public async Task<ErrorOr<VanSalesCustomerAccountsViewModel>> Handle(
        GetVanSalesCustomerAccountsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var accountsTask = accountService.GetAccountsAsync(
                request.RouteCustomerId,
                request.IncludeInactive,
                cancellationToken);

            var routeCustomersTask = routeCustomerService.GetRouteCustomersAsync(activeOnly: true);

            await Task.WhenAll(accountsTask, routeCustomersTask);

            return new VanSalesCustomerAccountsViewModel
            {
                Accounts = await accountsTask,
                RouteCustomers = (await routeCustomersTask)
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading van sales customer sign-ins");
            return Errors.VanSalesCustomerAccount.LoadFailed("Failed to load customer sign-ins.");
        }
    }
}
