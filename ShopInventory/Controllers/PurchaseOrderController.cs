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
/// Controller for Purchase Order operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class PurchaseOrderController : ControllerBase
{
    private readonly IPurchaseOrderService _purchaseOrderService;
    private readonly ILogger<PurchaseOrderController> _logger;

    public PurchaseOrderController(IPurchaseOrderService purchaseOrderService, ILogger<PurchaseOrderController> logger)
    {
        _purchaseOrderService = purchaseOrderService;
        _logger = logger;
    }

    /// <summary>
    /// Get all purchase orders with pagination and filtering
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.ViewPurchaseOrders)]
    [ProducesResponseType(typeof(PurchaseOrderListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] PurchaseOrderStatus? status = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _purchaseOrderService.GetAllAsync(page, pageSize, status, cardCode, fromDate, toDate, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get purchase order by ID
    /// </summary>
    [HttpGet("{id}")]
    [RequirePermission(Permission.ViewPurchaseOrders)]
    [ProducesResponseType(typeof(PurchaseOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var order = await _purchaseOrderService.GetByIdAsync(id, cancellationToken);
        if (order == null)
            return NotFound(new { message = $"Purchase order with ID {id} not found" });

        return Ok(order);
    }

    /// <summary>
    /// Get purchase order by order number
    /// </summary>
    [HttpGet("number/{orderNumber}")]
    [RequirePermission(Permission.ViewPurchaseOrders)]
    [ProducesResponseType(typeof(PurchaseOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByOrderNumber(string orderNumber, CancellationToken cancellationToken)
    {
        var order = await _purchaseOrderService.GetByOrderNumberAsync(orderNumber, cancellationToken);
        if (order == null)
            return NotFound(new { message = $"Purchase order '{orderNumber}' not found" });

        return Ok(order);
    }

    /// <summary>
    /// Create a new purchase order
    /// </summary>
    [HttpPost]
    [RequirePermission(Permission.CreatePurchaseOrders)]
    [ProducesResponseType(typeof(PurchaseOrderDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreatePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetCurrentUserId();

        try
        {
            var order = await _purchaseOrderService.CreateAsync(request, userId, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating purchase order");
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    /// <summary>
    /// Update a purchase order
    /// </summary>
    [HttpPut("{id}")]
    [RequirePermission(Permission.EditPurchaseOrders)]
    [ProducesResponseType(typeof(PurchaseOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, [FromBody] CreatePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var order = await _purchaseOrderService.UpdateAsync(id, request, cancellationToken);
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    /// <summary>
    /// Update purchase order status
    /// </summary>
    [HttpPatch("{id}/status")]
    [RequirePermission(Permission.EditPurchaseOrders)]
    [ProducesResponseType(typeof(PurchaseOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdatePurchaseOrderStatusRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        try
        {
            var order = await _purchaseOrderService.UpdateStatusAsync(id, request.Status, userId, request.Comments, cancellationToken);
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    /// <summary>
    /// Approve a purchase order
    /// </summary>
    [HttpPost("{id}/approve")]
    [RequirePermission(Permission.ApprovePurchaseOrders)]
    [ProducesResponseType(typeof(PurchaseOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Approve(int id, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        try
        {
            var order = await _purchaseOrderService.ApproveAsync(id, userId, cancellationToken);
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    /// <summary>
    /// Receive items against a purchase order (goods receipt)
    /// </summary>
    [HttpPost("{id}/receive")]
    [RequirePermission(Permission.ReceivePurchaseOrders)]
    [ProducesResponseType(typeof(PurchaseOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReceiveItems(int id, [FromBody] ReceivePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetCurrentUserId();

        try
        {
            var order = await _purchaseOrderService.ReceiveItemsAsync(id, request, userId, cancellationToken);
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    /// <summary>
    /// Delete a purchase order
    /// </summary>
    [HttpDelete("{id}")]
    [RequirePermission(Permission.DeletePurchaseOrders)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _purchaseOrderService.DeleteAsync(id, cancellationToken);
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
