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
/// Controller for Sales Order operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class SalesOrderController : ControllerBase
{
    private readonly ISalesOrderService _salesOrderService;
    private readonly ILogger<SalesOrderController> _logger;

    public SalesOrderController(ISalesOrderService salesOrderService, ILogger<SalesOrderController> logger)
    {
        _salesOrderService = salesOrderService;
        _logger = logger;
    }

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
        var result = await _salesOrderService.GetAllAsync(page, pageSize, status, cardCode, fromDate, toDate, source, cancellationToken);
        return Ok(result);
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
        var order = await _salesOrderService.GetByIdAsync(id, cancellationToken);
        if (order == null)
            return NotFound(new { message = $"Sales order with ID {id} not found" });

        return Ok(order);
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
        var order = await _salesOrderService.GetByOrderNumberAsync(orderNumber, cancellationToken);
        if (order == null)
            return NotFound(new { message = $"Sales order '{orderNumber}' not found" });

        return Ok(order);
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
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        try
        {
            var order = await _salesOrderService.CreateAsync(request, userId.Value, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating sales order");
            var message = ex.InnerException?.Message ?? ex.Message;
            return BadRequest(new ErrorResponseDto { Message = message });
        }
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
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var order = await _salesOrderService.UpdateAsync(id, request, cancellationToken);
            return Ok(order);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return Conflict(new ErrorResponseDto { Message = "This order was modified by another user. Please reload and try again." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
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

        try
        {
            var order = await _salesOrderService.UpdateStatusAsync(id, request.Status, userId.Value, request.Comments, cancellationToken);
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
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

        try
        {
            var order = await _salesOrderService.ApproveAsync(id, userId.Value, cancellationToken);
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
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

        try
        {
            var invoice = await _salesOrderService.ConvertToInvoiceAsync(id, userId.Value, cancellationToken);
            return Ok(invoice);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
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

        try
        {
            var order = await _salesOrderService.PostToSAPAsync(id, userId.Value, cancellationToken);
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting sales order {Id} to SAP", id);
            return StatusCode(502, new ErrorResponseDto { Message = $"Failed to post to SAP: {ex.Message}" });
        }
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
        try
        {
            var deleted = await _salesOrderService.DeleteAsync(id, cancellationToken);
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
