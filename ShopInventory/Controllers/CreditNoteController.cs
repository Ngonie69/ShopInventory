using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using System.Security.Claims;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for Credit Note operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class CreditNoteController : ControllerBase
{
    private readonly ICreditNoteService _creditNoteService;
    private readonly ILogger<CreditNoteController> _logger;

    public CreditNoteController(ICreditNoteService creditNoteService, ILogger<CreditNoteController> logger)
    {
        _creditNoteService = creditNoteService;
        _logger = logger;
    }

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
        var result = await _creditNoteService.GetAllAsync(page, pageSize, status, cardCode, fromDate, toDate, cancellationToken);
        return Ok(result);
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
        var creditNote = await _creditNoteService.GetByIdAsync(id, cancellationToken);
        if (creditNote == null)
            return NotFound(new { message = $"Credit note with ID {id} not found" });

        return Ok(creditNote);
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
        var creditNote = await _creditNoteService.GetByCreditNoteNumberAsync(creditNoteNumber, cancellationToken);
        if (creditNote == null)
            return NotFound(new { message = $"Credit note '{creditNoteNumber}' not found" });

        return Ok(creditNote);
    }

    /// <summary>
    /// Get all credit notes associated with a specific invoice
    /// </summary>
    [HttpGet("by-invoice/{invoiceId}")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(CreditNotesByInvoiceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByInvoiceId(int invoiceId, CancellationToken cancellationToken)
    {
        var creditNotes = await _creditNoteService.GetByInvoiceIdAsync(invoiceId, cancellationToken);
        var response = new CreditNotesByInvoiceResponse
        {
            InvoiceId = invoiceId,
            HasExistingCreditNotes = creditNotes.Any(),
            TotalCreditedAmount = creditNotes.Sum(cn => cn.DocTotal),
            CreditNotes = creditNotes
        };
        return Ok(response);
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
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        try
        {
            var creditNote = await _creditNoteService.CreateAsync(request, userId.Value, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = creditNote.Id }, creditNote);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating credit note");
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
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

        try
        {
            var lines = request.Lines?.Select(l => new CreateCreditNoteLineRequest
            {
                ItemCode = l.ItemCode ?? "",
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent,
                TaxPercent = l.TaxPercent,
                WarehouseCode = l.WarehouseCode,
                ReturnReason = l.ReturnReason
            }).ToList() ?? new List<CreateCreditNoteLineRequest>();

            var creditNote = await _creditNoteService.CreateFromInvoiceAsync(invoiceId, lines, request.Reason ?? "", userId.Value, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = creditNote.Id }, creditNote);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating credit note from invoice {InvoiceId}", invoiceId);
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
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

        try
        {
            var creditNote = await _creditNoteService.UpdateStatusAsync(id, request.Status, userId.Value, cancellationToken);
            return Ok(creditNote);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
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

        try
        {
            var creditNote = await _creditNoteService.ApproveAsync(id, userId.Value, cancellationToken);
            return Ok(creditNote);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
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
        try
        {
            var deleted = await _creditNoteService.DeleteAsync(id, cancellationToken);
            if (!deleted)
                return NotFound();

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
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
