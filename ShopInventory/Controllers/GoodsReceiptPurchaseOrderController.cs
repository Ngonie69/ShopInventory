using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.GoodsReceiptPurchaseOrders.Commands.CreateGoodsReceiptPurchaseOrder;
using ShopInventory.Features.GoodsReceiptPurchaseOrders.Queries.GetGoodsReceiptPurchaseOrderByDocEntry;
using ShopInventory.Features.GoodsReceiptPurchaseOrders.Queries.GetGoodsReceiptPurchaseOrders;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class GoodsReceiptPurchaseOrderController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permission.ViewGoodsReceiptPurchaseOrders)]
    [ProducesResponseType(typeof(GoodsReceiptPurchaseOrderListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetGoodsReceiptPurchaseOrdersQuery(page, pageSize, cardCode, fromDate, toDate), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}")]
    [RequirePermission(Permission.ViewGoodsReceiptPurchaseOrders)]
    [ProducesResponseType(typeof(GoodsReceiptPurchaseOrderDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByDocEntry(int docEntry, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetGoodsReceiptPurchaseOrderByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpPost]
    [RequirePermission(Permission.CreateGoodsReceiptPurchaseOrders)]
    [ProducesResponseType(typeof(GoodsReceiptPurchaseOrderDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateGoodsReceiptPurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new CreateGoodsReceiptPurchaseOrderCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetByDocEntry), new { docEntry = value.DocEntry }, value),
            Problem);
    }
}