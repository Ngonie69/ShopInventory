using MediatR;
using ShopInventory.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.Quotations.Commands.ApproveQuotation;
using ShopInventory.Features.Quotations.Commands.ConvertToSalesOrder;
using ShopInventory.Features.Quotations.Commands.CreateQuotation;
using ShopInventory.Features.Quotations.Commands.DeleteQuotation;
using ShopInventory.Features.Quotations.Commands.UpdateQuotation;
using ShopInventory.Features.Quotations.Commands.UpdateQuotationStatus;
using ShopInventory.Features.Quotations.Queries.GetAllQuotations;
using ShopInventory.Features.Quotations.Queries.GetQuotationById;
using ShopInventory.Features.Quotations.Queries.GetQuotationByNumber;
using ShopInventory.Features.Quotations.Queries.GetQuotationFromSAPByDocEntry;
using ShopInventory.Features.Quotations.Queries.GetQuotationsFromSAP;
using ShopInventory.Models.Entities;
using System.Security.Claims;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class QuotationController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permission.ViewInvoices)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] QuotationStatus? status = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetAllQuotationsQuery(page, pageSize, status, cardCode, fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("sap")]
    [RequirePermission(Permission.ViewInvoices)]
    public async Task<IActionResult> GetFromSAP(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetQuotationsFromSAPQuery(page, pageSize, cardCode, fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("sap/{docEntry}")]
    [RequirePermission(Permission.ViewInvoices)]
    public async Task<IActionResult> GetFromSAPByDocEntry(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetQuotationFromSAPByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{id}")]
    [RequirePermission(Permission.ViewInvoices)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetQuotationByIdQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("number/{quotationNumber}")]
    [RequirePermission(Permission.ViewInvoices)]
    public async Task<IActionResult> GetByQuotationNumber(string quotationNumber, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetQuotationByNumberQuery(quotationNumber), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost]
    [RequirePermission(Permission.CreateInvoices)]
    public async Task<IActionResult> Create([FromBody] CreateQuotationRequest request, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var result = await mediator.Send(new CreateQuotationCommand(request, userId), cancellationToken);
        return result.Match(value => CreatedAtAction(nameof(GetById), new { id = value.Id }, value), errors => Problem(errors));
    }

    [HttpPut("{id}")]
    [RequirePermission(Permission.EditInvoices)]
    public async Task<IActionResult> Update(int id, [FromBody] CreateQuotationRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateQuotationCommand(id, request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPatch("{id}/status")]
    [RequirePermission(Permission.EditInvoices)]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateQuotationStatusRequest request, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var result = await mediator.Send(new UpdateQuotationStatusCommand(id, request.Status, userId, request.Comments), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("{id}/approve")]
    [RequirePermission(Permission.EditInvoices)]
    public async Task<IActionResult> Approve(int id, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var result = await mediator.Send(new ApproveQuotationCommand(id, userId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("{id}/convert-to-sales-order")]
    [RequirePermission(Permission.CreateInvoices)]
    public async Task<IActionResult> ConvertToSalesOrder(int id, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var result = await mediator.Send(new ConvertToSalesOrderCommand(id, userId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("{id}")]
    [RequirePermission(Permission.DeleteInvoices)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteQuotationCommand(id), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }
}
