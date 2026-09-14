using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.Vending.Commands.ImportVendors;
using ShopInventory.Features.Vending.Queries.GetVendingOverview;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// The vending operation: depots, their cashier accounts and the vendors they serve.
/// </summary>
/// <remarks>
/// One vendor at a time is added, edited and removed through <c>api/route-customers</c>, which already
/// enforces who may touch which business partner's list and the vendor code convention. The one write
/// here is the bulk upload, which spans depots and so has no single route to sit under.
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

    /// <summary>
    /// Checks a sheet of vendors (<c>validateOnly: true</c>) or adds it. A problem on any row is reported
    /// against that row with a 200, and nothing is saved.
    /// </summary>
    [HttpPost("vendors/import")]
    [RequirePermission(Permission.CreateCustomers)]
    public async Task<IActionResult> ImportVendors(
        [FromBody] ImportVendorsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new ImportVendorsCommand(request, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }
}
