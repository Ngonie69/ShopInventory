using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// Saves a number on a customer with the consent it is given under — the one way a contact is
/// written, whether from the customer's page or from a send that chose to keep its number.
/// </summary>
internal static class CustomerWhatsAppContactWriter
{
    /// <summary>
    /// Stages the contact; the caller saves. The same number already on the customer is renewed in
    /// place — fresh consent, and any earlier opt-out lifted — rather than added a second time.
    /// </summary>
    public static async Task<ErrorOr<CustomerWhatsAppContactEntity>> StageAsync(
        ApplicationDbContext context,
        ContactOwner owner,
        string phoneE164,
        string? contactName,
        bool autoSendInvoices,
        WhatsAppConsentSource consentSource,
        string? consentNote,
        Guid? actorUserId,
        string actorName,
        int maxContactsPerOwner,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var live = context.CustomerWhatsAppContacts
            .AsTracking()
            .Where(contact => contact.RemovedAtUtc == null);

        live = owner.CardCode is not null
            ? live.Where(contact => contact.CardCode == owner.CardCode)
            : live.Where(contact => contact.RouteCustomerId == owner.RouteCustomerId);

        var existing = await live.ToListAsync(cancellationToken);
        var contact = existing.FirstOrDefault(row => row.PhoneE164 == phoneE164);

        if (contact is null)
        {
            if (existing.Count >= Math.Max(1, maxContactsPerOwner))
            {
                return Errors.CustomerDocuments.ContactLimitReached(Math.Max(1, maxContactsPerOwner));
            }

            contact = new CustomerWhatsAppContactEntity
            {
                CardCode = owner.CardCode,
                RouteCustomerId = owner.RouteCustomerId,
                PhoneE164 = phoneE164,
                OwnerName = owner.Name,
                ConsentRecordedBy = actorName,
                CreatedAtUtc = nowUtc
            };
            context.CustomerWhatsAppContacts.Add(contact);
        }

        contact.OwnerName = Truncate(owner.Name, 200)!;
        contact.ContactName = Truncate(Clean(contactName), 100) ?? contact.ContactName;
        contact.AutoSendInvoices = autoSendInvoices;
        contact.ConsentSource = consentSource;
        contact.ConsentNote = Truncate(Clean(consentNote), 500);
        contact.ConsentRecordedAtUtc = nowUtc;
        contact.ConsentRecordedByUserId = actorUserId;
        contact.ConsentRecordedBy = Truncate(actorName, 100)!;
        contact.OptedOutAtUtc = null;
        contact.OptedOutSource = null;
        contact.OptedOutBy = null;
        contact.UpdatedAtUtc = nowUtc;
        contact.UpdatedBy = Truncate(actorName, 100);

        return contact;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
