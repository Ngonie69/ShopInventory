using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.CheckWhatsAppNumber;

public sealed class CheckWhatsAppNumberHandler(
    ApplicationDbContext context,
    IOptions<CustomerDocumentDeliverySettings> options)
    : IRequestHandler<CheckWhatsAppNumberQuery, ErrorOr<WhatsAppNumberCheckResultDto>>
{
    public async Task<ErrorOr<WhatsAppNumberCheckResultDto>> Handle(
        CheckWhatsAppNumberQuery query,
        CancellationToken cancellationToken)
    {
        var result = new WhatsAppNumberCheckResultDto { Input = query.Phone ?? string.Empty };

        if (!WhatsAppRecipients.TryNormalise(query.Phone, options.Value.DefaultCountryCode, out var phoneE164))
        {
            result.Message = "That is not a phone number. Write it as 0771234567 or +263771234567.";
            return result;
        }

        result.IsValid = true;
        result.PhoneE164 = phoneE164;
        result.PhoneMasked = WhatsAppRecipients.Mask(phoneE164);

        result.OptedOut = await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .AnyAsync(contact => contact.PhoneE164 == phoneE164
                && contact.OptedOutAtUtc != null
                && contact.RemovedAtUtc == null, cancellationToken);

        var cardCode = string.IsNullOrWhiteSpace(query.CardCode) ? null : query.CardCode.Trim();
        if (cardCode is not null || query.RouteCustomerId is not null)
        {
            result.ExistingContactId = await context.CustomerWhatsAppContacts
                .AsNoTracking()
                .Where(contact => contact.PhoneE164 == phoneE164
                    && contact.RemovedAtUtc == null
                    && (cardCode != null ? contact.CardCode == cardCode : contact.RouteCustomerId == query.RouteCustomerId))
                .Select(contact => (int?)contact.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        result.Message = result.OptedOut
            ? "The customer asked not to receive documents on this number."
            : result.ExistingContactId is not null
                ? "This number is already saved on the customer."
                : null;

        return result;
    }
}
