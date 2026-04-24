using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseQuotations.Commands.CreatePurchaseQuotation;
using ShopInventory.Features.PurchaseQuotations.Queries.GetPurchaseQuotationByDocEntry;
using ShopInventory.Features.PurchaseQuotations.Queries.GetPurchaseQuotations;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class PurchaseQuotationController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permission.ViewPurchaseQuotations)]
    [ProducesResponseType(typeof(PurchaseQuotationListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPurchaseQuotationsQuery(page, pageSize, cardCode, fromDate, toDate), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}")]
    [RequirePermission(Permission.ViewPurchaseQuotations)]
    [ProducesResponseType(typeof(PurchaseQuotationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByDocEntry(int docEntry, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPurchaseQuotationByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpPost]
    [RequirePermission(Permission.CreatePurchaseQuotations)]
    [ProducesResponseType(typeof(PurchaseQuotationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreatePurchaseQuotationRequest request, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new CreatePurchaseQuotationCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetByDocEntry), new { docEntry = value.DocEntry }, value),
            Problem);
    }
}