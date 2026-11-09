using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContactRegister;

public sealed class GetCustomerWhatsAppContactRegisterHandler(ApplicationDbContext context)
    : IRequestHandler<GetCustomerWhatsAppContactRegisterQuery, ErrorOr<CustomerWhatsAppContactRegisterDto>>
{
    public async Task<ErrorOr<CustomerWhatsAppContactRegisterDto>> Handle(
        GetCustomerWhatsAppContactRegisterQuery query,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var all = context.CustomerWhatsAppContacts.AsNoTracking();

        var contacts = query.ContactId is { } contactId
            ? all.Where(contact => contact.Id == contactId)
            : Filter(all, query);

        var total = await contacts.CountAsync(cancellationToken);

        // Newest consent first: the number someone just added is the one people come looking for.
        var rows = await contacts
            .OrderByDescending(contact => contact.ConsentRecordedAtUtc)
            .ThenByDescending(contact => contact.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(contact => new
            {
                Contact = contact,
                RouteCustomerCode = contact.RouteCustomer == null ? null : contact.RouteCustomer.Code
            })
            .ToListAsync(cancellationToken);

        var ids = rows.Select(row => row.Contact.Id).ToList();
        var sends = await SendsAsync(ids, cancellationToken);
        var project = CustomerDocumentProjections.Contact.Compile();

        return new CustomerWhatsAppContactRegisterDto
        {
            Items = rows.Select(row =>
            {
                sends.TryGetValue(row.Contact.Id, out var sent);
                return new CustomerWhatsAppContactRegisterItemDto
                {
                    Contact = project(row.Contact),
                    RouteCustomerCode = row.RouteCustomerCode,
                    SentCount = sent?.Sent ?? 0,
                    LastSentAtUtc = sent?.LastSentAtUtc,
                    WaitingCount = sent?.Waiting ?? 0
                };
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            Summary = await SummaryAsync(all, cancellationToken)
        };
    }

    private static IQueryable<CustomerWhatsAppContactEntity> Filter(
        IQueryable<CustomerWhatsAppContactEntity> contacts,
        GetCustomerWhatsAppContactRegisterQuery query)
    {
        contacts = (query.State ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "removed" => contacts.Where(contact => contact.RemovedAtUtc != null),
            "auto" => contacts.Where(contact => contact.RemovedAtUtc == null && contact.OptedOutAtUtc == null && contact.AutoSendInvoices),
            "request" => contacts.Where(contact => contact.RemovedAtUtc == null && contact.OptedOutAtUtc == null && !contact.AutoSendInvoices),
            "optedout" => contacts.Where(contact => contact.RemovedAtUtc == null && contact.OptedOutAtUtc != null),
            "notonwhatsapp" => contacts.Where(contact => contact.RemovedAtUtc == null && contact.WhatsAppExists == false),
            _ => contacts.Where(contact => contact.RemovedAtUtc == null)
        };

        if (Enum.TryParse<WhatsAppConsentSource>(query.Source, ignoreCase: true, out var source))
        {
            contacts = contacts.Where(contact => contact.ConsentSource == source);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Lower-cased on both sides rather than ILIKE, so the same query runs on PostgreSQL and on
            // the SQLite the tests use.
            var lowered = query.Search.Trim().ToLowerInvariant();
            var digits = PhoneDigits(query.Search);

            contacts = contacts.Where(contact =>
                contact.OwnerName.ToLower().Contains(lowered)
                || (contact.CardCode != null && contact.CardCode.ToLower().Contains(lowered))
                || (contact.ContactName != null && contact.ContactName.ToLower().Contains(lowered))
                || contact.ConsentRecordedBy.ToLower().Contains(lowered)
                || (contact.RouteCustomer != null && contact.RouteCustomer.Code.ToLower().Contains(lowered))
                || (digits != null && contact.PhoneE164.Contains(digits)));
        }

        return contacts;
    }

    /// <summary>
    /// The digits to look for in a stored +263… number, or null when the search is not a number. A
    /// local number's leading 0 is the trunk prefix, which the stored form replaces with the country
    /// code, so it is dropped.
    /// </summary>
    internal static string? PhoneDigits(string search)
    {
        var trimmed = search.Trim();
        if (trimmed.Any(char.IsLetter))
            return null;

        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length < 3)
            return null;

        return digits.Length >= 9 && digits[0] == '0' ? digits.TrimStart('0') : digits;
    }

    private async Task<Dictionary<int, SendCounts>> SendsAsync(List<int> contactIds, CancellationToken cancellationToken)
    {
        if (contactIds.Count == 0)
            return [];

        // Statuses written out rather than an array's Contains: an enum stored through a string
        // converter is the shape providers have translated least reliably.
        return await context.CustomerDocumentDeliveries
            .AsNoTracking()
            .Where(delivery => delivery.ContactId != null && contactIds.Contains(delivery.ContactId.Value))
            .GroupBy(delivery => delivery.ContactId!.Value)
            .Select(group => new SendCounts(
                group.Key,
                group.Count(delivery =>
                    delivery.Status == CustomerDocumentDeliveryStatus.Sent
                    || delivery.Status == CustomerDocumentDeliveryStatus.SentUnconfirmed),
                group.Max(delivery => delivery.SentAtUtc),
                group.Count(delivery =>
                    delivery.Status == CustomerDocumentDeliveryStatus.Pending
                    || delivery.Status == CustomerDocumentDeliveryStatus.Preparing
                    || delivery.Status == CustomerDocumentDeliveryStatus.WaitingForFiscal
                    || delivery.Status == CustomerDocumentDeliveryStatus.Sending)))
            .ToDictionaryAsync(counts => counts.ContactId, cancellationToken);
    }

    private static async Task<CustomerWhatsAppContactRegisterSummaryDto> SummaryAsync(
        IQueryable<CustomerWhatsAppContactEntity> all,
        CancellationToken cancellationToken)
    {
        var states = await all
            .Select(contact => new
            {
                Removed = contact.RemovedAtUtc != null,
                OptedOut = contact.OptedOutAtUtc != null,
                contact.AutoSendInvoices,
                NotOnWhatsApp = contact.WhatsAppExists == false
            })
            .ToListAsync(cancellationToken);

        var live = states.Where(state => !state.Removed).ToList();

        return new CustomerWhatsAppContactRegisterSummaryDto
        {
            Linked = live.Count,
            Automatic = live.Count(state => !state.OptedOut && state.AutoSendInvoices),
            OnRequest = live.Count(state => !state.OptedOut && !state.AutoSendInvoices),
            OptedOut = live.Count(state => state.OptedOut),
            NotOnWhatsApp = live.Count(state => state.NotOnWhatsApp),
            Removed = states.Count - live.Count
        };
    }

    private sealed record SendCounts(int ContactId, int Sent, DateTime? LastSentAtUtc, int Waiting);
}
