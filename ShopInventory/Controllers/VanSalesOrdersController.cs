using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Models;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Features.VanSalesOrders.Commands.ConvertVanSalesOrderToSalesOrder;
using ShopInventory.Features.VanSalesOrders.Commands.RecordVanSalesOrderDelivery;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesOrdersForRoute;
using ShopInventory.Models.Entities;

namespace ShopInventory.Controllers;

/// <summary>
/// The operator's view of orders van sales customers placed for themselves.
/// </summary>
/// <remarks>
/// Staff-facing, and behind "ApiAccess" rather than the customer policy. A customer reaching these
/// actions could read a whole route's trading, record their own delivery, or push their order into
/// the ERP — so the two surfaces are separate controllers with separate policies rather than one
/// controller with per-action attributes, which is a mistake that only has to be made once.
/// </remarks>
[Route("api/van-sales-orders")]
[Authorize(Policy = "ApiAccess")]
public class VanSalesOrdersController(ISender mediator) : ApiControllerBase
{
    /// <summary>
    /// What a van has been asked to carry: totals to load, and the orders behind them.
    /// </summary>
    [HttpGet("route-load")]
    [RequirePermission(Permission.ViewSalesOrders)]
    [ProducesResponseType(typeof(VanSalesRouteLoadResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRouteLoad(
        [FromQuery] string? assignedBusinessPartnerCode = null,
        [FromQuery] string? routeCode = null,
        [FromQuery] DateTime? visitDate = null,
        [FromQuery] VanSalesOrderStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetVanSalesOrdersForRouteQuery(assignedBusinessPartnerCode, routeCode, visitDate, status),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>Record what was actually delivered against an order.</summary>
    [HttpPost("{orderId:int}/delivery")]
    [RequirePermission(Permission.EditSalesOrders)]
    [ProducesResponseType(typeof(VanSalesOrderResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordDelivery(
        int orderId,
        [FromBody] RecordVanSalesOrderDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new RecordVanSalesOrderDeliveryCommand(
                orderId,
                request.Lines
                    .Select(l => new RecordVanSalesDeliveryLine(l.LineNumber, l.QuantityFulfilled))
                    .ToList(),
                GetAuthenticatedUserId()),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Turn a customer's order into a sales order.
    /// </summary>
    /// <remarks>
    /// The only crossing between this intake and the tables that feed SAP. Requires the permission
    /// to create sales orders, because that is exactly what it does.
    /// </remarks>
    [HttpPost("{orderId:int}/convert")]
    [RequirePermission(Permission.CreateSalesOrders)]
    [ProducesResponseType(typeof(VanSalesOrderConversionResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Convert(int orderId, CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();

        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new ConvertVanSalesOrderToSalesOrderCommand(orderId, userId.Value),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    private Guid? GetAuthenticatedUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var userId) ? userId : null;
    }
}
