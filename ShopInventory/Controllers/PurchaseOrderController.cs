using MediatR;
using ShopInventory.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseOrders.Commands.ApprovePurchaseOrder;
using ShopInventory.Features.PurchaseOrders.Commands.CreatePurchaseOrder;
using ShopInventory.Features.PurchaseOrders.Commands.DeletePurchaseOrder;
using ShopInventory.Features.PurchaseOrders.Commands.ReceivePurchaseOrder;
using ShopInventory.Features.PurchaseOrders.Commands.UpdatePurchaseOrder;
using ShopInventory.Features.PurchaseOrders.Commands.UpdatePurchaseOrderStatus;
using ShopInventory.Features.PurchaseOrders.Queries.GetAllPurchaseOrders;
using ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderById;
using ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderByNumber;
using ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderFromSAPByDocEntry;
using ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrdersFromSAP;
using ShopInventory.Models.Entities;
using System.Security.Claims;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class PurchaseOrderController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permission.ViewPurchaseOrders)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] PurchaseOrderStatus? status = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetAllPurchaseOrdersQuery(page, pageSize, status, cardCode, fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("sap")]
    [RequirePermission(Permission.ViewPurchaseOrders)]
    public async Task<IActionResult> GetFromSAP(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPurchaseOrdersFromSAPQuery(page, pageSize, cardCode, fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("sap/{docEntry}")]
    [RequirePermission(Permission.ViewPurchaseOrders)]
    public async Task<IActionResult> GetFromSAPByDocEntry(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPurchaseOrderFromSAPByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{id}")]
    [RequirePermission(Permission.ViewPurchaseOrders)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPurchaseOrderByIdQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("number/{orderNumber}")]
    [RequirePermission(Permission.ViewPurchaseOrders)]
    public async Task<IActionResult> GetByOrderNumber(string orderNumber, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPurchaseOrderByNumberQuery(orderNumber), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost]
    [RequirePermission(Permission.CreatePurchaseOrders)]
    public async Task<IActionResult> Create([FromBody] CreatePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? userId = Guid.TryParse(userIdClaim, out var uid) ? uid : null;

        var result = await mediator.Send(new CreatePurchaseOrderCommand(request, userId), cancellationToken);
        return result.Match(value => CreatedAtAction(nameof(GetById), new { id = value.Id }, value), errors => Problem(errors));
    }

    [HttpPut("{id}")]
    [RequirePermission(Permission.EditPurchaseOrders)]
    public async Task<IActionResult> Update(int id, [FromBody] CreatePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdatePurchaseOrderCommand(id, request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPatch("{id}/status")]
    [RequirePermission(Permission.EditPurchaseOrders)]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdatePurchaseOrderStatusRequest request, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? userId = Guid.TryParse(userIdClaim, out var uid) ? uid : null;

        var result = await mediator.Send(new UpdatePurchaseOrderStatusCommand(id, request.Status, userId, request.Comments), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("{id}/approve")]
    [RequirePermission(Permission.ApprovePurchaseOrders)]
    public async Task<IActionResult> Approve(int id, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? userId = Guid.TryParse(userIdClaim, out var uid) ? uid : null;

        var result = await mediator.Send(new ApprovePurchaseOrderCommand(id, userId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("{id}/receive")]
    [RequirePermission(Permission.ReceivePurchaseOrders)]
    public async Task<IActionResult> ReceiveItems(int id, [FromBody] ReceivePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? userId = Guid.TryParse(userIdClaim, out var uid) ? uid : null;

        var result = await mediator.Send(new ReceivePurchaseOrderCommand(id, request, userId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("{id}")]
    [RequirePermission(Permission.DeletePurchaseOrders)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeletePurchaseOrderCommand(id), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }
}
