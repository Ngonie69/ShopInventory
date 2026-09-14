using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.Vending.Queries.GetVendingOverview;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// The vending operation: depots, their cashier accounts and the vendors they serve.
/// </summary>
/// <remarks>
/// Read only. Vendors are added, edited and removed through <c>api/route-customers</c>, which already
/// enforces who may touch which business partner's list; a second write path here would be a second
/// set of rules to keep in step.
///
/// Role-gated rather than on <c>customers.view</c>: the overview names every vending account and the
/// warehouse it draws from, and a cart vendor holds that permission to read its own vendor list, not
/// every depot's staff.
/// </remarks>
[Route("api/vending")]
[Authorize(Policy = "ApiAccess")]
[Authorize(Roles = $"{ApplicationRoles.Admin},{ApplicationRoles.Manager},{ApplicationRoles.Cashier}")]
public class VendingController(ISender mediator) : ApiControllerBase
{
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetVendingOverviewQuery(), cancellationToken);
        return result.Match(Ok, Problem);
    }
}
