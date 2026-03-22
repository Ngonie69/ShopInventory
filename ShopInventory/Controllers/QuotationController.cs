using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using System.Security.Claims;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class QuotationController : ControllerBase
{
    private readonly IQuotationService _quotationService;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<QuotationController> _logger;

    public QuotationController(IQuotationService quotationService, ISAPServiceLayerClient sapClient, ILogger<QuotationController> logger)
    {
        _quotationService = quotationService;
        _sapClient = sapClient;
        _logger = logger;
    }

    [HttpGet]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(QuotationListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] QuotationStatus? status = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _quotationService.GetAllAsync(page, pageSize, status, cardCode, fromDate, toDate, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get quotations from SAP with pagination and optional filtering
    /// </summary>
    [HttpGet("sap")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(QuotationListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFromSAP(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            List<SAPQuotation> sapQuotations;

            if (fromDate.HasValue && toDate.HasValue)
            {
                sapQuotations = await _sapClient.GetQuotationsByDateRangeAsync(fromDate.Value, toDate.Value, cancellationToken);
            }
            else if (!string.IsNullOrEmpty(cardCode))
            {
                sapQuotations = await _sapClient.GetQuotationsByCustomerAsync(cardCode, cancellationToken);
            }
            else
            {
                sapQuotations = await _sapClient.GetPagedQuotationsFromSAPAsync(page, pageSize, cancellationToken);
            }

            // Apply additional filters
            if (!string.IsNullOrEmpty(cardCode) && fromDate.HasValue)
            {
                sapQuotations = sapQuotations.Where(q => q.CardCode == cardCode).ToList();
            }

            var totalCount = sapQuotations.Count;

            // If we fetched by date range or customer (non-paged), apply local pagination
            if (fromDate.HasValue || !string.IsNullOrEmpty(cardCode))
            {
                sapQuotations = sapQuotations
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();
            }

            var quotations = sapQuotations.Select(MapSAPToQuotationDto).ToList();

            var result = new QuotationListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                Quotations = quotations
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching quotations from SAP");
            return StatusCode(500, new { message = "Failed to fetch quotations from SAP", error = ex.Message });
        }
    }

    /// <summary>
    /// Get a specific quotation from SAP by document entry
    /// </summary>
    [HttpGet("sap/{docEntry}")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(QuotationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFromSAPByDocEntry(int docEntry, CancellationToken cancellationToken)
    {
        try
        {
            var sapQuotation = await _sapClient.GetQuotationByDocEntryAsync(docEntry, cancellationToken);
            if (sapQuotation == null)
                return NotFound(new { message = $"Quotation with DocEntry {docEntry} not found in SAP" });

            return Ok(MapSAPToQuotationDto(sapQuotation));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching quotation {DocEntry} from SAP", docEntry);
            return StatusCode(500, new { message = "Failed to fetch quotation from SAP", error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(QuotationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var quotation = await _quotationService.GetByIdAsync(id, cancellationToken);
        if (quotation == null)
            return NotFound(new { message = $"Quotation with ID {id} not found" });

        return Ok(quotation);
    }

    [HttpGet("number/{quotationNumber}")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(QuotationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByQuotationNumber(string quotationNumber, CancellationToken cancellationToken)
    {
        var quotation = await _quotationService.GetByQuotationNumberAsync(quotationNumber, cancellationToken);
        if (quotation == null)
            return NotFound(new { message = $"Quotation '{quotationNumber}' not found" });

        return Ok(quotation);
    }

    [HttpPost]
    [RequirePermission(Permission.CreateInvoices)]
    [ProducesResponseType(typeof(QuotationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateQuotationRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        try
        {
            var quotation = await _quotationService.CreateAsync(request, userId.Value, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = quotation.Id }, quotation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating quotation");
            var message = ex.InnerException?.Message ?? ex.Message;
            return BadRequest(new ErrorResponseDto { Message = message });
        }
    }

    [HttpPut("{id}")]
    [RequirePermission(Permission.EditInvoices)]
    [ProducesResponseType(typeof(QuotationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, [FromBody] CreateQuotationRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var quotation = await _quotationService.UpdateAsync(id, request, cancellationToken);
            return Ok(quotation);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    [HttpPatch("{id}/status")]
    [RequirePermission(Permission.EditInvoices)]
    [ProducesResponseType(typeof(QuotationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateQuotationStatusRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        try
        {
            var quotation = await _quotationService.UpdateStatusAsync(id, request.Status, userId.Value, request.Comments, cancellationToken);
            return Ok(quotation);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    [HttpPost("{id}/approve")]
    [RequirePermission(Permission.EditInvoices)]
    [ProducesResponseType(typeof(QuotationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Approve(int id, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        try
        {
            var quotation = await _quotationService.ApproveAsync(id, userId.Value, cancellationToken);
            return Ok(quotation);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    [HttpPost("{id}/convert-to-sales-order")]
    [RequirePermission(Permission.CreateInvoices)]
    [ProducesResponseType(typeof(SalesOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConvertToSalesOrder(int id, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        try
        {
            var salesOrder = await _quotationService.ConvertToSalesOrderAsync(id, userId.Value, cancellationToken);
            return Ok(salesOrder);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    [RequirePermission(Permission.DeleteInvoices)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _quotationService.DeleteAsync(id, cancellationToken);
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

    private static QuotationDto MapSAPToQuotationDto(SAPQuotation sap)
    {
        var isCancelled = sap.Cancelled == "tYES";
        var isClosed = sap.DocumentStatus == "bost_Close";

        QuotationStatus status;
        if (isCancelled)
            status = QuotationStatus.Cancelled;
        else if (isClosed)
            status = QuotationStatus.Converted;
        else
            status = QuotationStatus.Approved;

        DateTime.TryParse(sap.DocDate, out var quotationDate);
        DateTime.TryParse(sap.DocDueDate, out var validUntil);

        var lines = sap.DocumentLines?.Select((l, idx) => new QuotationLineDto
        {
            Id = idx,
            LineNum = l.LineNum,
            ItemCode = l.ItemCode ?? "",
            ItemDescription = l.ItemDescription ?? "",
            Quantity = l.Quantity ?? 0,
            UnitPrice = l.UnitPrice ?? 0,
            LineTotal = l.LineTotal ?? 0,
            WarehouseCode = l.WarehouseCode,
            DiscountPercent = l.DiscountPercent ?? 0,
            UoMCode = l.UoMCode
        }).ToList() ?? new List<QuotationLineDto>();

        return new QuotationDto
        {
            Id = sap.DocEntry,
            SAPDocEntry = sap.DocEntry,
            SAPDocNum = sap.DocNum,
            QuotationNumber = $"SAP-{sap.DocNum}",
            QuotationDate = quotationDate,
            ValidUntil = validUntil == default ? null : validUntil,
            CardCode = sap.CardCode ?? "",
            CardName = sap.CardName,
            CustomerRefNo = sap.NumAtCard,
            Status = status,
            Currency = sap.DocCurrency ?? "USD",
            SubTotal = (sap.DocTotal ?? 0) - (sap.VatSum ?? 0),
            TaxAmount = sap.VatSum ?? 0,
            DiscountPercent = sap.DiscountPercent ?? 0,
            DiscountAmount = sap.TotalDiscount ?? 0,
            DocTotal = sap.DocTotal ?? 0,
            Comments = sap.Comments,
            ShipToAddress = sap.Address,
            BillToAddress = sap.Address2,
            Lines = lines,
            CreatedByUserName = "SAP",
            IsSynced = true
        };
    }
}
