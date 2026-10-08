using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.OptOutCustomerWhatsAppContact;

/// <summary>
/// Opts a number out everywhere it is saved, and withdraws what was waiting to go to it.
/// </summary>
/// <remarks>
/// A person asks to stop receiving messages on their phone, not on one of a shop's currency cards, so
/// the opt-out follows the number across every customer it is on. The rows stay — opted out, not
/// removed — so the number is never quietly saved again without fresh consent.
/// </remarks>
public sealed class OptOutCustomerWhatsAppContactHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<OptOutCustomerWhatsAppContactHandler> logger)
    : IRequestHandler<OptOutCustomerWhatsAppContactCommand, ErrorOr<List<CustomerWhatsAppContactDto>>>
{
    private static readonly Func<CustomerWhatsAppContactEntity, CustomerWhatsAppContactDto> ToDto =
        CustomerDocumentProjections.Contact.Compile();

    public async Task<ErrorOr<List<CustomerWhatsAppContactDto>>> Handle(
        OptOutCustomerWhatsAppContactCommand command,
        CancellationToken cancellationToken)
    {
        var actorName = await CustomerDocumentActor.ResolveNameAsync(context, command.UserId, cancellationToken);
        if (actorName is null)
            return Errors.CustomerDocuments.UserNotFound;

        var phone = await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .Where(contact => contact.Id == command.ContactId && contact.RemovedAtUtc == null)
            .Select(contact => contact.PhoneE164)
            .FirstOrDefaultAsync(cancellationToken);

        if (phone is null)
            return Errors.CustomerDocuments.ContactNotFound(command.ContactId);

        var now = DateTime.UtcNow;
        var rows = await context.CustomerWhatsAppContacts
            .AsTracking()
            .Where(contact => contact.PhoneE164 == phone && contact.RemovedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var row in rows.Where(row => row.OptedOutAtUtc is null))
        {
            row.OptedOutAtUtc = now;
            row.OptedOutSource = WhatsAppOptOutSource.Web;
            row.OptedOutBy = actorName;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = actorName;
        }

        await context.SaveChangesAsync(cancellationToken);

        await CustomerDocumentDeliveryCancellation.CancelWaitingAsync(
            context,
            delivery => delivery.RecipientE164 == phone,
            "The customer asked not to receive documents on this number before it went.",
            actorName,
            now,
            cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.OptOutCustomerWhatsAppContact,
                "CustomerWhatsAppContact",
                command.ContactId.ToString(),
                $"Opted {WhatsAppRecipients.Mask(phone)} out of documents on {rows.Count} customer card(s)",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the opt-out of WhatsApp contact {ContactId}", command.ContactId);
        }

        return rows.Select(ToDto).ToList();
    }
}
