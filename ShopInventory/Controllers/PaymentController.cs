using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;

namespace ShopInventory.Controllers;

/// <summary>
/// Handles payment processing through multiple Zimbabwean payment gateways (PayNow, Innbucks, Ecocash)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentController : ControllerBase
{
    private readonly IPaymentGatewayService _paymentService;
    private readonly ILogger<PaymentController> _logger;

    public PaymentController(IPaymentGatewayService paymentService, ILogger<PaymentController> logger)
    {
        _paymentService = paymentService;
        _logger = logger;
    }

    /// <summary>
    /// Get available payment providers
    /// </summary>
    [HttpGet("providers")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get payment providers", Description = "Retrieves list of available payment providers and their configuration")]
    [SwaggerResponse(200, "Payment providers retrieved successfully", typeof(PaymentProvidersResponse))]
    public async Task<ActionResult<PaymentProvidersResponse>> GetProviders()
    {
        var providers = await _paymentService.GetAvailableProvidersAsync();
        return Ok(providers);
    }

    /// <summary>
    /// Initiate a payment
    /// </summary>
    /// <param name="request">Payment initiation request</param>
    [HttpPost("initiate")]
    [SwaggerOperation(Summary = "Initiate payment", Description = "Initiates a payment through the specified payment provider")]
    [SwaggerResponse(200, "Payment initiated successfully", typeof(InitiatePaymentResponse))]
    [SwaggerResponse(400, "Invalid request")]
    public async Task<ActionResult<InitiatePaymentResponse>> InitiatePayment([FromBody] InitiatePaymentRequest request)
    {
        try
        {
            var username = User.FindFirstValue(ClaimTypes.Name);
            var response = await _paymentService.InitiatePaymentAsync(request, username);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating payment");
            return StatusCode(500, new { message = "Failed to initiate payment" });
        }
    }

    /// <summary>
    /// Check payment status
    /// </summary>
    /// <param name="id">Transaction ID</param>
    [HttpGet("{id}/status")]
    [SwaggerOperation(Summary = "Check payment status", Description = "Retrieves the current status of a payment transaction")]
    [SwaggerResponse(200, "Payment status retrieved", typeof(PaymentStatusResponse))]
    [SwaggerResponse(404, "Transaction not found")]
    public async Task<ActionResult<PaymentStatusResponse>> CheckStatus(int id)
    {
        var status = await _paymentService.CheckStatusAsync(id);
        if (status == null)
        {
            return NotFound(new { message = "Transaction not found" });
        }
        return Ok(status);
    }

    /// <summary>
    /// Get payment transactions
    /// </summary>
    /// <param name="provider">Filter by provider</param>
    /// <param name="status">Filter by status</param>
    /// <param name="page">Page number</param>
    /// <param name="pageSize">Page size</param>
    [HttpGet("transactions")]
    [SwaggerOperation(Summary = "Get transactions", Description = "Retrieves payment transaction history with filtering")]
    [SwaggerResponse(200, "Transactions retrieved", typeof(PaymentTransactionListResponse))]
    public async Task<ActionResult<PaymentTransactionListResponse>> GetTransactions(
        [FromQuery] string? provider = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var transactions = await _paymentService.GetTransactionsAsync(provider, status, page, pageSize);
        return Ok(transactions);
    }

    /// <summary>
    /// Cancel a pending payment
    /// </summary>
    /// <param name="id">Transaction ID</param>
    [HttpPost("{id}/cancel")]
    [SwaggerOperation(Summary = "Cancel payment", Description = "Cancels a pending payment transaction")]
    [SwaggerResponse(200, "Payment cancelled")]
    [SwaggerResponse(400, "Cannot cancel payment")]
    [SwaggerResponse(404, "Transaction not found")]
    public async Task<IActionResult> CancelPayment(int id)
    {
        var result = await _paymentService.CancelPaymentAsync(id);
        if (!result)
        {
            return BadRequest(new { message = "Cannot cancel this payment. It may have already been processed." });
        }
        return Ok(new { message = "Payment cancelled successfully" });
    }

    /// <summary>
    /// Refund a successful payment
    /// </summary>
    /// <param name="id">Transaction ID</param>
    /// <param name="amount">Optional partial refund amount</param>
    [HttpPost("{id}/refund")]
    [Authorize(Policy = "AdminOnly")]
    [SwaggerOperation(Summary = "Refund payment", Description = "Refunds a successful payment (Admin only)")]
    [SwaggerResponse(200, "Payment refunded")]
    [SwaggerResponse(400, "Cannot refund payment")]
    [SwaggerResponse(404, "Transaction not found")]
    public async Task<IActionResult> RefundPayment(int id, [FromQuery] decimal? amount = null)
    {
        var result = await _paymentService.RefundPaymentAsync(id, amount);
        if (!result)
        {
            return BadRequest(new { message = "Cannot refund this payment. It may not have been completed." });
        }
        return Ok(new { message = "Payment refunded successfully" });
    }

    /// <summary>
    /// Payment callback endpoint for PayNow
    /// </summary>
    [HttpPost("callback/paynow")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "PayNow callback", Description = "Receives payment notifications from PayNow")]
    public async Task<IActionResult> PayNowCallback([FromForm] PaymentCallbackPayload payload)
    {
        _logger.LogInformation("Received PayNow callback");
        var result = await _paymentService.ProcessCallbackAsync("PayNow", payload);
        return result ? Ok() : BadRequest();
    }

    /// <summary>
    /// Payment callback endpoint for Innbucks
    /// </summary>
    [HttpPost("callback/innbucks")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Innbucks callback", Description = "Receives payment notifications from Innbucks")]
    public async Task<IActionResult> InnbucksCallback([FromBody] PaymentCallbackPayload payload)
    {
        _logger.LogInformation("Received Innbucks callback");
        var result = await _paymentService.ProcessCallbackAsync("Innbucks", payload);
        return result ? Ok() : BadRequest();
    }

    /// <summary>
    /// Payment callback endpoint for Ecocash
    /// </summary>
    [HttpPost("callback/ecocash")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Ecocash callback", Description = "Receives payment notifications from Ecocash")]
    public async Task<IActionResult> EcocashCallback([FromBody] PaymentCallbackPayload payload)
    {
        _logger.LogInformation("Received Ecocash callback");
        var result = await _paymentService.ProcessCallbackAsync("Ecocash", payload);
        return result ? Ok() : BadRequest();
    }
}
