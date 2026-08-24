using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Features.VanSalesOrders.Commands.CancelVanSalesCustomerOrder;
using ShopInventory.Features.VanSalesOrders.Commands.SubmitVanSalesCustomerOrder;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrderByClientRequestId;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrderById;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrders;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// Orders van sales customers place for themselves.
/// </summary>
/// <remarks>
/// Every action takes the customer from the token and nothing from the body or the route identifies
/// whose order this is. An account id a caller can supply is an account id a caller can change, and
/// these endpoints would then read and cancel other shops' orders.
/// </remarks>
[Route("api/van-sales-customer/orders")]
[Authorize(Policy = "VanSalesCustomerAccess")]
public class VanSalesCustomerOrdersController(ISender mediator) : ApiControllerBase
{
    /// <summary>
    /// Place an order.
    /// </summary>
    /// <remarks>
    /// Idempotent on <c>ClientRequestId</c>: sending the same key again returns the original order
    /// with 200 rather than creating a second one or reporting a conflict. A handset that never saw
    /// the first reply is not in error, and telling it so would make it retry forever.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(VanSalesOrderResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Submit(
        [FromBody] SubmitVanSalesCustomerOrderRequest request,
        CancellationToken cancellationToken)
    {
        var accountId = GetAuthenticatedCustomerAccountId();
        if (accountId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new SubmitVanSalesCustomerOrderCommand(
                accountId.Value,
                request.ClientRequestId,
                request.Lines
                    .Select(l => new SubmitVanSalesCustomerOrderLine(l.ItemCode, l.Quantity))
                    .ToList(),
                request.RequestedVisitDate,
                request.CustomerNotes,
                request.SubmittedAtUtc,
                request.DeviceInfo,
                request.AppVersion,
                request.Latitude,
                request.Longitude),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>The signed-in shop's order history, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(VanSalesOrderListResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrders(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var accountId = GetAuthenticatedCustomerAccountId();
        if (accountId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetVanSalesCustomerOrdersQuery(accountId.Value, page, pageSize),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Resolve the order an idempotency key created, if it created one.
    /// </summary>
    /// <remarks>
    /// The reconciliation an offline handset depends on. After a submit whose reply was lost, the
    /// app asks this before deciding whether to send again — a 404 means no order exists and it is
    /// safe to retry, and anything else means the order is already placed.
    /// <para>
    /// Declared above the <c>{orderId:int}</c> route so a client request id is never mistaken for
    /// an order id.
    /// </para>
    /// </remarks>
    [HttpGet("by-client-request/{clientRequestId}")]
    [ProducesResponseType(typeof(VanSalesOrderResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByClientRequestId(
        string clientRequestId,
        CancellationToken cancellationToken)
    {
        var accountId = GetAuthenticatedCustomerAccountId();
        if (accountId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetVanSalesCustomerOrderByClientRequestIdQuery(accountId.Value, clientRequestId),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>One of the signed-in shop's orders.</summary>
    [HttpGet("{orderId:int}")]
    [ProducesResponseType(typeof(VanSalesOrderResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrder(int orderId, CancellationToken cancellationToken)
    {
        var accountId = GetAuthenticatedCustomerAccountId();
        if (accountId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetVanSalesCustomerOrderByIdQuery(accountId.Value, orderId),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>Withdraw an order, if the cut-off for its delivery has not passed.</summary>
    [HttpPost("{orderId:int}/cancel")]
    [ProducesResponseType(typeof(VanSalesOrderResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(
        int orderId,
        [FromBody] CancelVanSalesCustomerOrderRequest request,
        CancellationToken cancellationToken)
    {
        var accountId = GetAuthenticatedCustomerAccountId();
        if (accountId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new CancelVanSalesCustomerOrderCommand(accountId.Value, orderId, request.Reason),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// The calling customer, taken from the token and from nowhere else.
    /// </summary>
    private int? GetAuthenticatedCustomerAccountId()
    {
        var claim = User.FindFirstValue(VanSalesCustomerClaims.AccountId);
        return int.TryParse(claim, out var accountId) ? accountId : null;
    }
}
