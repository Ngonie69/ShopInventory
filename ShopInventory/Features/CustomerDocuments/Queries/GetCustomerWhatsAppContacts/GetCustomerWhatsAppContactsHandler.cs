using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContacts;

public sealed class GetCustomerWhatsAppContactsHandler(ApplicationDbContext context)
    : IRequestHandler<GetCustomerWhatsAppContactsQuery, ErrorOr<List<CustomerWhatsAppContactDto>>>
{
    public async Task<ErrorOr<List<CustomerWhatsAppContactDto>>> Handle(
        GetCustomerWhatsAppContactsQuery query,
        CancellationToken cancellationToken)
    {
        var cardCode = string.IsNullOrWhiteSpace(query.CardCode) ? null : query.CardCode.Trim();

        if ((cardCode is null) == (query.RouteCustomerId is null))
            return Errors.CustomerDocuments.OwnerRequired;

        var contacts = context.CustomerWhatsAppContacts.AsNoTracking();

        contacts = cardCode is not null
            ? contacts.Where(contact => contact.CardCode == cardCode)
            : contacts.Where(contact => contact.RouteCustomerId == query.RouteCustomerId);

        if (!query.IncludeRemoved)
            contacts = contacts.Where(contact => contact.RemovedAtUtc == null);

        return await contacts
            .OrderBy(contact => contact.RemovedAtUtc != null)
            .ThenBy(contact => contact.OptedOutAtUtc != null)
            .ThenBy(contact => contact.CreatedAtUtc)
            .Select(CustomerDocumentProjections.Contact)
            .ToListAsync(cancellationToken);
    }
}
