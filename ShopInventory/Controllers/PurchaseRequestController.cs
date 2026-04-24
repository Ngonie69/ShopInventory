using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseRequests.Commands.CreatePurchaseRequest;
using ShopInventory.Features.PurchaseRequests.Queries.GetPurchaseRequestByDocEntry;
using ShopInventory.Features.PurchaseRequests.Queries.GetPurchaseRequests;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class PurchaseRequestController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permission.ViewPurchaseRequests)]
    [ProducesResponseType(typeof(PurchaseRequestListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPurchaseRequestsQuery(page, pageSize, fromDate, toDate), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}")]
    [RequirePermission(Permission.ViewPurchaseRequests)]
    [ProducesResponseType(typeof(PurchaseRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByDocEntry(int docEntry, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPurchaseRequestByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpPost]
    [RequirePermission(Permission.CreatePurchaseRequests)]
    [ProducesResponseType(typeof(PurchaseRequestDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreatePurchaseRequestRequest request, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new CreatePurchaseRequestCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetByDocEntry), new { docEntry = value.DocEntry }, value),
            Problem);
    }
}