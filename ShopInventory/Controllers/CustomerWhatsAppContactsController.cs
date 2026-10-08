using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Commands.OptOutCustomerWhatsAppContact;
using ShopInventory.Features.CustomerDocuments.Commands.RecheckCustomerWhatsAppContact;
using ShopInventory.Features.CustomerDocuments.Commands.RemoveCustomerWhatsAppContact;
using ShopInventory.Features.CustomerDocuments.Commands.SaveCustomerWhatsAppContact;
using ShopInventory.Features.CustomerDocuments.Commands.UpdateCustomerWhatsAppContact;
using ShopInventory.Features.CustomerDocuments.Queries.CheckWhatsAppNumber;
using ShopInventory.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContacts;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// The WhatsApp numbers customers gave to receive their documents on, with their consent.
/// </summary>
/// <remarks>
/// Its own permission, <c>customers.whatsapp.manage</c>, rather than <c>customers.edit</c>: saving a
/// number here points every new invoice of that customer at a phone. The person saving it is taken
/// from the token, because they are the witness to the customer's consent.
/// </remarks>
[Route("api/customer-whatsapp-contacts")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public sealed class CustomerWhatsAppContactsController(ISender mediator) : ApiControllerBase
{
    /// <summary>The numbers saved on one customer: <c>cardCode</c> or <c>routeCustomerId</c>, not both.</summary>
    [HttpGet]
    [RequirePermission(Permission.ManageCustomerWhatsApp, Permission.SendInvoicesWhatsApp)]
    [ProducesResponseType(typeof(List<CustomerWhatsAppContactDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetContacts(
        [FromQuery] string? cardCode = null,
        [FromQuery] int? routeCustomerId = null,
        [FromQuery] bool includeRemoved = false,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetCustomerWhatsAppContactsQuery(cardCode, routeCustomerId, includeRemoved),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Whether a number is one, whether its owner opted out of documents, and whether it is already
    /// saved on the customer. Asks nothing of WhatsApp.
    /// </summary>
    [HttpGet("phone-check")]
    [RequirePermission(Permission.ManageCustomerWhatsApp, Permission.SendInvoicesWhatsApp)]
    [ProducesResponseType(typeof(WhatsAppNumberCheckResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckPhone(
        [FromQuery] string phone,
        [FromQuery] string? cardCode = null,
        [FromQuery] int? routeCustomerId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new CheckWhatsAppNumberQuery(phone, cardCode, routeCustomerId), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Saves a number on a customer — and on the same shop's cards in its other currencies when
    /// <c>alsoApplyToCardCodes</c> names them — with the customer's consent.
    /// </summary>
    [HttpPost]
    [RequirePermission(Permission.ManageCustomerWhatsApp)]
    [ProducesResponseType(typeof(List<CustomerWhatsAppContactDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save(
        [FromBody] SaveCustomerWhatsAppContactRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new SaveCustomerWhatsAppContactCommand(request, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>Changes a saved number's contact name and whether it receives invoices on its own.</summary>
    [HttpPut("{id:int}")]
    [RequirePermission(Permission.ManageCustomerWhatsApp)]
    [ProducesResponseType(typeof(CustomerWhatsAppContactDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdateCustomerWhatsAppContactRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new UpdateCustomerWhatsAppContactCommand(id, request, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>Stops documents to this number on every customer it is saved on, and withdraws what was waiting for it.</summary>
    [HttpPost("{id:int}/opt-out")]
    [RequirePermission(Permission.ManageCustomerWhatsApp)]
    [ProducesResponseType(typeof(List<CustomerWhatsAppContactDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OptOut(int id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new OptOutCustomerWhatsAppContactCommand(id, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>Asks WhatsApp now whether the number has an account. Counts against the daily check cap.</summary>
    [HttpPost("{id:int}/check")]
    [RequirePermission(Permission.ManageCustomerWhatsApp)]
    [ProducesResponseType(typeof(CustomerWhatsAppContactDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Recheck(int id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new RecheckCustomerWhatsAppContactCommand(id, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>Takes the number off this customer only. The record stays for the documents already sent to it.</summary>
    [HttpDelete("{id:int}")]
    [RequirePermission(Permission.ManageCustomerWhatsApp)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(int id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new RemoveCustomerWhatsAppContactCommand(id, userId.Value), cancellationToken);
        return result.Match(_ => NoContent(), Problem);
    }
}
