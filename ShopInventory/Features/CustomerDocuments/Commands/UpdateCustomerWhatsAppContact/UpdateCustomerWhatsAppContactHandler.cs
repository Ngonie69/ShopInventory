using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.UpdateCustomerWhatsAppContact;

/// <summary>
/// Changes who a saved number belongs to and whether new invoices go there on their own. The number
/// and its consent stay as they were saved: a different number is a different consent.
/// </summary>
/// <remarks>
/// Turning automatic sending off withdraws the automatic sends already waiting for this number, so the
/// switch means "stop" at once and not "stop after the queue drains".
/// </remarks>
public sealed class UpdateCustomerWhatsAppContactHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<UpdateCustomerWhatsAppContactHandler> logger)
    : IRequestHandler<UpdateCustomerWhatsAppContactCommand, ErrorOr<CustomerWhatsAppContactDto>>
{
    private static readonly Func<CustomerWhatsAppContactEntity, CustomerWhatsAppContactDto> ToDto =
        CustomerDocumentProjections.Contact.Compile();

    public async Task<ErrorOr<CustomerWhatsAppContactDto>> Handle(
        UpdateCustomerWhatsAppContactCommand command,
        CancellationToken cancellationToken)
    {
        var actorName = await CustomerDocumentActor.ResolveNameAsync(context, command.UserId, cancellationToken);
        if (actorName is null)
            return Errors.CustomerDocuments.UserNotFound;

        var contact = await context.CustomerWhatsAppContacts
            .AsTracking()
            .FirstOrDefaultAsync(row => row.Id == command.ContactId && row.RemovedAtUtc == null, cancellationToken);

        if (contact is null)
            return Errors.CustomerDocuments.ContactNotFound(command.ContactId);

        var now = DateTime.UtcNow;
        var turnedOff = contact.AutoSendInvoices && !command.Request.AutoSendInvoices;

        contact.ContactName = string.IsNullOrWhiteSpace(command.Request.ContactName) ? null : command.Request.ContactName.Trim();
        contact.AutoSendInvoices = command.Request.AutoSendInvoices;
        contact.UpdatedAtUtc = now;
        contact.UpdatedBy = actorName;

        await context.SaveChangesAsync(cancellationToken);

        if (turnedOff)
        {
            await CustomerDocumentDeliveryCancellation.CancelWaitingAsync(
                context,
                delivery => delivery.ContactId == contact.Id && delivery.Trigger == CustomerDocumentDeliveryTrigger.Auto,
                "Automatic sending was turned off for this number before it went.",
                actorName,
                now,
                cancellationToken);
        }

        try
        {
            await auditService.LogAsync(
                AuditActions.SaveCustomerWhatsAppContact,
                "CustomerWhatsAppContact",
                contact.Id.ToString(),
                $"Updated {WhatsAppRecipients.Mask(contact.PhoneE164)} on {contact.CardCode ?? $"route customer {contact.RouteCustomerId}"}; "
                + $"automatic invoices {(contact.AutoSendInvoices ? "on" : "off")}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit WhatsApp contact {ContactId}", contact.Id);
        }

        return ToDto(contact);
    }
}
