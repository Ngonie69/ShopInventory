using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Features.CreditNotes.Commands.ApproveCreditNote;
using ShopInventory.Features.CreditNotes.Commands.CreateCreditNote;
using ShopInventory.Features.CreditNotes.Commands.CreateCreditNoteFromInvoice;
using ShopInventory.Features.CreditNotes.Commands.DeleteCreditNote;
using ShopInventory.Features.CreditNotes.Commands.UpdateCreditNoteStatus;
using ShopInventory.Features.CreditNotes.Queries.GetAllCreditNotes;
using ShopInventory.Features.CreditNotes.Queries.GetCreditNoteById;
using ShopInventory.Features.CreditNotes.Queries.GetCreditNoteByNumber;
using ShopInventory.Features.CreditNotes.Queries.GetCreditNotesByInvoice;
using ShopInventory.Models.Entities;
using System.Security.Claims;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for Credit Note operations
/// </summary>
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class CreditNoteController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get all credit notes with pagination and filtering
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(CreditNoteListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] CreditNoteStatus? status = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetAllCreditNotesQuery(page, pageSize, status, cardCode, fromDate, toDate),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get credit note by ID
    /// </summary>
    [HttpGet("{id}")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(CreditNoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCreditNoteByIdQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get credit note by credit note number
    /// </summary>
    [HttpGet("number/{creditNoteNumber}")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(CreditNoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByCreditNoteNumber(string creditNoteNumber, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCreditNoteByNumberQuery(creditNoteNumber), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get all credit notes associated with a specific invoice
    /// </summary>
    [HttpGet("by-invoice/{invoiceId}")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(CreditNotesByInvoiceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByInvoiceId(int invoiceId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCreditNotesByInvoiceQuery(invoiceId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Create a new credit note
    /// </summary>
    [HttpPost]
    [RequirePermission(Permission.CreateInvoices)]
    [ProducesResponseType(typeof(CreditNoteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateCreditNoteRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new CreateCreditNoteCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetById), new { id = value.Id }, value),
            errors => Problem(errors));
    }

    /// <summary>
    /// Create a credit note from an existing invoice
    /// </summary>
    [HttpPost("from-invoice/{invoiceId}")]
    [RequirePermission(Permission.CreateInvoices)]
    [ProducesResponseType(typeof(CreditNoteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateFromInvoice(
        int invoiceId,
        [FromBody] CreateCreditNoteFromInvoiceApiRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(
            new CreateCreditNoteFromInvoiceCommand(invoiceId, request, userId.Value),
            cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetById), new { id = value.Id }, value),
            errors => Problem(errors));
    }

    /// <summary>
    /// Update credit note status
    /// </summary>
    [HttpPatch("{id}/status")]
    [RequirePermission(Permission.EditInvoices)]
    [ProducesResponseType(typeof(CreditNoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateCreditNoteStatusRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(
            new UpdateCreditNoteStatusCommand(id, request.Status, userId.Value),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Approve a credit note
    /// </summary>
    [HttpPost("{id}/approve")]
    [RequirePermission(Permission.EditInvoices)]
    [ProducesResponseType(typeof(CreditNoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Approve(int id, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new ApproveCreditNoteCommand(id, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Delete a credit note
    /// </summary>
    [HttpDelete("{id}")]
    [RequirePermission(Permission.DeleteInvoices)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteCreditNoteCommand(id), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}

/// <summary>
/// Request DTO for creating credit note from invoice via API
/// </summary>
public class CreateCreditNoteFromInvoiceApiRequest
{
    public string? Reason { get; set; }
    public List<CreditNoteLineApiRequest>? Lines { get; set; }
}

/// <summary>
/// Credit note line request for API
/// </summary>
public class CreditNoteLineApiRequest
{
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public string? WarehouseCode { get; set; }
    public string? ReturnReason { get; set; }
    public int? OriginalInvoiceLineId { get; set; }
}

/// <summary>
/// Response DTO for credit notes by invoice
/// </summary>
public class CreditNotesByInvoiceResponse
{
    public int InvoiceId { get; set; }
    public bool HasExistingCreditNotes { get; set; }
    public decimal TotalCreditedAmount { get; set; }
    public List<CreditNoteDto> CreditNotes { get; set; } = new();
}

/// <summary>
/// Request to update credit note status
/// </summary>
public class UpdateCreditNoteStatusRequest
{
    public CreditNoteStatus Status { get; set; }
}
