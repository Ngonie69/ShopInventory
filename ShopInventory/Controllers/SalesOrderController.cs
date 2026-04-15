using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Features.SalesOrders.Commands.ApproveSalesOrder;
using ShopInventory.Features.SalesOrders.Commands.ConvertToInvoice;
using ShopInventory.Features.SalesOrders.Commands.CreateSalesOrder;
using ShopInventory.Features.SalesOrders.Commands.DeleteSalesOrder;
using ShopInventory.Features.SalesOrders.Commands.PostToSAP;
using ShopInventory.Features.SalesOrders.Commands.UpdateSalesOrder;
using ShopInventory.Features.SalesOrders.Commands.UpdateSalesOrderStatus;
using ShopInventory.Features.SalesOrders.Queries.GetAllSalesOrders;
using ShopInventory.Features.SalesOrders.Queries.GetSalesOrderById;
using ShopInventory.Features.SalesOrders.Queries.GetSalesOrderByNumber;
using ShopInventory.Models.Entities;
using System.Security.Claims;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for Sales Order operations
/// </summary>
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class SalesOrderController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get all sales orders with pagination and filtering
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.ViewSalesOrders)]
    [ProducesResponseType(typeof(SalesOrderListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] SalesOrderStatus? status = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] SalesOrderSource? source = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetAllSalesOrdersQuery(page, pageSize, status, cardCode, fromDate, toDate, source),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get sales order by ID
    /// </summary>
    [HttpGet("{id}")]
    [RequirePermission(Permission.ViewSalesOrders)]
    [ProducesResponseType(typeof(SalesOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSalesOrderByIdQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get sales order by order number
    /// </summary>
    [HttpGet("number/{orderNumber}")]
    [RequirePermission(Permission.ViewSalesOrders)]
    [ProducesResponseType(typeof(SalesOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByOrderNumber(string orderNumber, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSalesOrderByNumberQuery(orderNumber), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Create a new sales order
    /// </summary>
    [HttpPost]
    [RequirePermission(Permission.CreateSalesOrders)]
    [ProducesResponseType(typeof(SalesOrderDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateSalesOrderRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new CreateSalesOrderCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetById), new { id = value.Id }, value),
            errors => Problem(errors));
    }

    /// <summary>
    /// Update a sales order (Draft only)
    /// </summary>
    [HttpPut("{id}")]
    [RequirePermission(Permission.EditSalesOrders)]
    [ProducesResponseType(typeof(SalesOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(int id, [FromBody] CreateSalesOrderRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateSalesOrderCommand(id, request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Update sales order status
    /// </summary>
    [HttpPatch("{id}/status")]
    [RequirePermission(Permission.EditSalesOrders)]
    [ProducesResponseType(typeof(SalesOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateSalesOrderStatusRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(
            new UpdateSalesOrderStatusCommand(id, request.Status, userId.Value, request.Comments),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Approve a sales order
    /// </summary>
    [HttpPost("{id}/approve")]
    [RequirePermission(Permission.ApproveSalesOrders)]
    [ProducesResponseType(typeof(SalesOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Approve(int id, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new ApproveSalesOrderCommand(id, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Convert sales order to invoice
    /// </summary>
    [HttpPost("{id}/convert-to-invoice")]
    [RequirePermission(Permission.CreateInvoices)]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConvertToInvoice(int id, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new ConvertToInvoiceCommand(id, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Post an approved sales order to SAP Business One
    /// </summary>
    [HttpPost("{id}/post-to-sap")]
    [RequirePermission(Permission.PostSalesOrdersToSAP)]
    [ProducesResponseType(typeof(SalesOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> PostToSAP(int id, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new PostToSAPCommand(id, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Delete a sales order
    /// </summary>
    [HttpDelete("{id}")]
    [RequirePermission(Permission.DeleteSalesOrders)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteSalesOrderCommand(id), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
