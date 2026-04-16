using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Payments.Commands.CancelPayment;
using ShopInventory.Features.Payments.Commands.InitiatePayment;
using ShopInventory.Features.Payments.Commands.ProcessEcocashCallback;
using ShopInventory.Features.Payments.Commands.ProcessInnbucksCallback;
using ShopInventory.Features.Payments.Commands.ProcessPayNowCallback;
using ShopInventory.Features.Payments.Commands.RefundPayment;
using ShopInventory.Features.Payments.Queries.CheckPaymentStatus;
using ShopInventory.Features.Payments.Queries.GetProviders;
using ShopInventory.Features.Payments.Queries.GetTransactions;
using Swashbuckle.AspNetCore.Annotations;

namespace ShopInventory.Controllers;

/// <summary>
/// Handles payment processing through multiple Zimbabwean payment gateways (PayNow, Innbucks, Ecocash)
/// </summary>
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class PaymentController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get available payment providers
    /// </summary>
    [HttpGet("providers")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get payment providers", Description = "Retrieves list of available payment providers and their configuration")]
    [SwaggerResponse(200, "Payment providers retrieved successfully", typeof(PaymentProvidersResponse))]
    public async Task<IActionResult> GetProviders(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetProvidersQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Initiate a payment
    /// </summary>
    [HttpPost("initiate")]
    [SwaggerOperation(Summary = "Initiate payment", Description = "Initiates a payment through the specified payment provider")]
    [SwaggerResponse(200, "Payment initiated successfully", typeof(InitiatePaymentResponse))]
    [SwaggerResponse(400, "Invalid request")]
    public async Task<IActionResult> InitiatePayment([FromBody] InitiatePaymentRequest request, CancellationToken cancellationToken)
    {
        var username = User.FindFirstValue(ClaimTypes.Name);
        var result = await mediator.Send(new InitiatePaymentCommand(request, username), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Check payment status
    /// </summary>
    [HttpGet("{id}/status")]
    [SwaggerOperation(Summary = "Check payment status", Description = "Retrieves the current status of a payment transaction")]
    [SwaggerResponse(200, "Payment status retrieved", typeof(PaymentStatusResponse))]
    [SwaggerResponse(404, "Transaction not found")]
    public async Task<IActionResult> CheckStatus(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CheckPaymentStatusQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get payment transactions
    /// </summary>
    [HttpGet("transactions")]
    [SwaggerOperation(Summary = "Get transactions", Description = "Retrieves payment transaction history with filtering")]
    [SwaggerResponse(200, "Transactions retrieved", typeof(PaymentTransactionListResponse))]
    public async Task<IActionResult> GetTransactions(
        [FromQuery] string? provider = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetTransactionsQuery(provider, status, page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Cancel a pending payment
    /// </summary>
    [HttpPost("{id}/cancel")]
    [SwaggerOperation(Summary = "Cancel payment", Description = "Cancels a pending payment transaction")]
    [SwaggerResponse(200, "Payment cancelled")]
    [SwaggerResponse(400, "Cannot cancel payment")]
    [SwaggerResponse(404, "Transaction not found")]
    public async Task<IActionResult> CancelPayment(int id, CancellationToken cancellationToken)
    {
        var username = User.FindFirstValue(ClaimTypes.Name);
        var result = await mediator.Send(new CancelPaymentCommand(id, username), cancellationToken);
        return result.Match(_ => Ok(new { message = "Payment cancelled successfully" }), errors => Problem(errors));
    }

    /// <summary>
    /// Refund a successful payment
    /// </summary>
    [HttpPost("{id}/refund")]
    [Authorize(Policy = "AdminOnly")]
    [SwaggerOperation(Summary = "Refund payment", Description = "Refunds a successful payment (Admin only)")]
    [SwaggerResponse(200, "Payment refunded")]
    [SwaggerResponse(400, "Cannot refund payment")]
    [SwaggerResponse(404, "Transaction not found")]
    public async Task<IActionResult> RefundPayment(int id, [FromQuery] decimal? amount = null, CancellationToken cancellationToken = default)
    {
        var username = User.FindFirstValue(ClaimTypes.Name);
        var result = await mediator.Send(new RefundPaymentCommand(id, amount, username), cancellationToken);
        return result.Match(_ => Ok(new { message = "Payment refunded successfully" }), errors => Problem(errors));
    }

    /// <summary>
    /// Payment callback endpoint for PayNow
    /// </summary>
    [HttpPost("callback/paynow")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "PayNow callback", Description = "Receives payment notifications from PayNow")]
    public async Task<IActionResult> PayNowCallback(CancellationToken cancellationToken)
    {
        var form = await Request.ReadFormAsync(cancellationToken);
        var formData = new Dictionary<string, string>();
        foreach (var pair in form)
            formData[pair.Key] = pair.Value.ToString();

        var result = await mediator.Send(new ProcessPayNowCallbackCommand(formData), cancellationToken);
        return result.Match(_ => Ok(), errors => Problem(errors));
    }

    /// <summary>
    /// Payment callback endpoint for Innbucks
    /// </summary>
    [HttpPost("callback/innbucks")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Innbucks callback", Description = "Receives payment notifications from Innbucks")]
    public async Task<IActionResult> InnbucksCallback([FromBody] PaymentCallbackPayload payload, CancellationToken cancellationToken)
    {
        var signature = GetCallbackSignature();
        var result = await mediator.Send(new ProcessInnbucksCallbackCommand(payload, signature), cancellationToken);
        return result.Match(_ => Ok(), errors => Problem(errors));
    }

    /// <summary>
    /// Payment callback endpoint for Ecocash
    /// </summary>
    [HttpPost("callback/ecocash")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Ecocash callback", Description = "Receives payment notifications from Ecocash")]
    public async Task<IActionResult> EcocashCallback([FromBody] PaymentCallbackPayload payload, CancellationToken cancellationToken)
    {
        var signature = GetCallbackSignature();
        var result = await mediator.Send(new ProcessEcocashCallbackCommand(payload, signature), cancellationToken);
        return result.Match(_ => Ok(), errors => Problem(errors));
    }

    private string? GetCallbackSignature()
    {
        return Request.Headers["X-Signature"].FirstOrDefault()
            ?? Request.Headers["X-Callback-Signature"].FirstOrDefault()
            ?? Request.Headers["X-Webhook-Signature"].FirstOrDefault()
            ?? Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
    }
}
