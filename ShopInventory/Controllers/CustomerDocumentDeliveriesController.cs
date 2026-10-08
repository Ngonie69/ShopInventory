using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Commands.CancelCustomerDocumentDelivery;
using ShopInventory.Features.CustomerDocuments.Commands.RequestInvoiceWhatsApp;
using ShopInventory.Features.CustomerDocuments.Commands.RetryCustomerDocumentDelivery;
using ShopInventory.Features.CustomerDocuments.Commands.UpdateCustomerDocumentDeliverySettings;
using ShopInventory.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveries;
using ShopInventory.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveryLog;
using ShopInventory.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveryStatus;
using ShopInventory.Features.CustomerDocuments.Queries.PreviewInvoiceWhatsAppDocument;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// Customer documents sent on WhatsApp: asking for a send, its history, and the administrators' view.
/// </summary>
/// <remarks>
/// Nothing here sends. A send is a delivery row that the clustered delivery job picks up — every send
/// keeps to the same pacing, fiscal check and duplicate guard, and a request answered by a node with no
/// gateway still goes. So the send endpoint answers 202: accepted, not delivered.
/// </remarks>
[Route("api/customer-document-deliveries")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public sealed class CustomerDocumentDeliveriesController(ISender mediator) : ApiControllerBase
{
    /// <summary>
    /// Every WhatsApp send of one document, newest first, recipients masked. Readable by anyone who may
    /// read invoices — a manager sees what was sent, and does not send.
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(List<CustomerDocumentDeliveryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDeliveries(
        [FromQuery] int? sapDocEntry = null,
        [FromQuery] int? desktopSaleId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetCustomerDocumentDeliveriesQuery(sapDocEntry, desktopSaleId), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>Queues an invoice for the customer's WhatsApp: saved numbers, a one-off number, or both.</summary>
    [HttpPost("invoices/{docEntry:int}")]
    [RequirePermission(Permission.SendInvoicesWhatsApp)]
    [ProducesResponseType(typeof(List<CustomerDocumentDeliveryDto>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RequestInvoice(
        int docEntry,
        [FromBody] RequestInvoiceWhatsAppRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new RequestInvoiceWhatsAppCommand(docEntry, request, userId.Value), cancellationToken);
        return result.Match(value => Accepted(value), Problem);
    }

    /// <summary>
    /// The PDF and caption sending this invoice would send right now, or why it would wait — no fiscal
    /// receipt yet, or one that belongs to another document.
    /// </summary>
    [HttpGet("invoices/{docEntry:int}/preview")]
    [RequirePermission(Permission.SendInvoicesWhatsApp)]
    [ProducesResponseType(typeof(InvoiceWhatsAppPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PreviewInvoice(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new PreviewInvoiceWhatsAppDocumentQuery(docEntry), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Sends a document again as a new delivery. When the first may already have arrived,
    /// <c>confirmNotReceived</c> must say the customer did not get it.
    /// </summary>
    [HttpPost("{id:long}/retry")]
    [RequirePermission(Permission.SendInvoicesWhatsApp)]
    [ProducesResponseType(typeof(CustomerDocumentDeliveryDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry(
        long id,
        [FromBody] RetryCustomerDocumentDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new RetryCustomerDocumentDeliveryCommand(id, request, userId.Value), cancellationToken);
        return result.Match(value => Accepted(value), Problem);
    }

    /// <summary>Withdraws a document that has not been handed to WhatsApp yet.</summary>
    [HttpPost("{id:long}/cancel")]
    [RequirePermission(Permission.SendInvoicesWhatsApp)]
    [ProducesResponseType(typeof(CustomerDocumentDeliveryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(long id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new CancelCustomerDocumentDeliveryCommand(id, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// The whole delivery log, newest first. <c>status</c> is a status name, or <c>attention</c> for
    /// held, uncertain and failed sends.
    /// </summary>
    [HttpGet("log")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(CustomerDocumentDeliveryPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLog(
        [FromQuery] string? status = null,
        [FromQuery] string? trigger = null,
        [FromQuery] string? search = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetCustomerDocumentDeliveryLogQuery(status, trigger, search, fromDate, toDate, page, pageSize),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>The switches, today's sends against the caps, what is waiting, and the gateway's sessions.</summary>
    [HttpGet("status")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(CustomerDocumentDeliveryStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCustomerDocumentDeliveryStatusQuery(), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Chooses the session documents are sent from, switches automatic sending, and sets its daily
    /// cap. A blank session stops all sending at once.
    /// </summary>
    [HttpPut("settings")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(CustomerDocumentDeliveryStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] UpdateCustomerDocumentDeliverySettingsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new UpdateCustomerDocumentDeliverySettingsCommand(request, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }
}
