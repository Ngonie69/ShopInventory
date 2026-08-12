using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Commands.DeleteRouteCustomer;
using ShopInventory.Features.RouteCustomers.Commands.UpdateRouteCustomer;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerProductMix;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomers;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSales;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSalesSummary;
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

    /// <summary>
    /// Every route customer's trading in a window, grouped by route.
    ///
    /// Also the source for the dormancy view: customers who bought nothing are returned rather than
    /// filtered out, with <c>daysSinceLastSale</c> null to distinguish "never bought" from "long gap".
    /// </summary>
    [HttpGet("sales-summary")]
    [RequirePermission(Permission.ViewCustomers)]
    public async Task<IActionResult> GetSalesSummary(
        [FromQuery] string? assignedBusinessPartnerCode = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int? dormantDays = null,
        [FromQuery] bool includeInactive = true,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetRouteCustomerSalesSummaryQuery(assignedBusinessPartnerCode, from, to, dormantDays, includeInactive),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>What route customers buy, one row per item, across a route or a single customer.</summary>
    [HttpGet("product-mix")]
    [RequirePermission(Permission.ViewCustomers)]
    public async Task<IActionResult> GetProductMix(
        [FromQuery] string? assignedBusinessPartnerCode = null,
        [FromQuery] int? routeCustomerId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int top = 0,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetRouteCustomerProductMixQuery(assignedBusinessPartnerCode, routeCustomerId, from, to, top),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>One route customer's sales, orders and product mix.</summary>
    [HttpGet("{id:int}/sales")]
    [RequirePermission(Permission.ViewCustomers)]
    public async Task<IActionResult> GetSales(
        int id,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetRouteCustomerSalesQuery(id, from, to),
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