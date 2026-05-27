using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Commands.DeleteRouteCustomer;
using ShopInventory.Features.RouteCustomers.Commands.UpdateRouteCustomer;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomers;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

[Route("api/route-customers")]
[Authorize(Policy = "ApiAccess")]
public class RouteCustomersController(ISender mediator) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permission.ViewCustomers)]
    public async Task<IActionResult> GetRouteCustomers(
        [FromQuery] string? assignedBusinessPartnerCode = null,
        [FromQuery] bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetRouteCustomersQuery(assignedBusinessPartnerCode, activeOnly),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpPut("{id:int}")]
    [RequirePermission(Permission.EditCustomers)]
    public async Task<IActionResult> UpdateRouteCustomer(
        int id,
        [FromBody] UpdateRouteCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new UpdateRouteCustomerCommand(id, request),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpDelete("{id:int}")]
    [RequirePermission(Permission.DeleteCustomers)]
    public async Task<IActionResult> DeleteRouteCustomer(
        int id,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new DeleteRouteCustomerCommand(id),
            cancellationToken);

        return result.Match(_ => NoContent(), Problem);
    }
}