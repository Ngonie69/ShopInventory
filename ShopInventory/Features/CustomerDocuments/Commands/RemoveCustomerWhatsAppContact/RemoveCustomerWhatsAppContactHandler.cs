using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.RemoveCustomerWhatsAppContact;

/// <summary>
/// Marks a contact removed and withdraws what was waiting to go to it under that contact. The row is
/// kept, because deliveries already made name the consent they were made under.
/// </summary>
public sealed class RemoveCustomerWhatsAppContactHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<RemoveCustomerWhatsAppContactHandler> logger)
    : IRequestHandler<RemoveCustomerWhatsAppContactCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RemoveCustomerWhatsAppContactCommand command,
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
        contact.RemovedAtUtc = now;
        contact.RemovedBy = actorName;
        contact.UpdatedAtUtc = now;
        contact.UpdatedBy = actorName;

        await context.SaveChangesAsync(cancellationToken);

        await CustomerDocumentDeliveryCancellation.CancelWaitingAsync(
            context,
            delivery => delivery.ContactId == contact.Id,
            "The number was taken off the customer before it went.",
            actorName,
            now,
            cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.RemoveCustomerWhatsAppContact,
                "CustomerWhatsAppContact",
                contact.Id.ToString(),
                $"Removed {WhatsAppRecipients.Mask(contact.PhoneE164)} from {contact.CardCode ?? $"route customer {contact.RouteCustomerId}"}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the removal of WhatsApp contact {ContactId}", contact.Id);
        }

        return Result.Success;
    }
}
