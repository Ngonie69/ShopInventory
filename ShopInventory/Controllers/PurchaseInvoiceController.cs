using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseInvoices.Commands.CreatePurchaseInvoice;
using ShopInventory.Features.PurchaseInvoices.Queries.GetPurchaseInvoiceByDocEntry;
using ShopInventory.Features.PurchaseInvoices.Queries.GetPurchaseInvoices;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class PurchaseInvoiceController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permission.ViewPurchaseInvoices)]
    [ProducesResponseType(typeof(PurchaseInvoiceListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPurchaseInvoicesQuery(page, pageSize, cardCode, fromDate, toDate), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}")]
    [RequirePermission(Permission.ViewPurchaseInvoices)]
    [ProducesResponseType(typeof(PurchaseInvoiceDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByDocEntry(int docEntry, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPurchaseInvoiceByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpPost]
    [RequirePermission(Permission.CreatePurchaseInvoices)]
    [ProducesResponseType(typeof(PurchaseInvoiceDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreatePurchaseInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new CreatePurchaseInvoiceCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetByDocEntry), new { docEntry = value.DocEntry }, value),
            Problem);
    }
}