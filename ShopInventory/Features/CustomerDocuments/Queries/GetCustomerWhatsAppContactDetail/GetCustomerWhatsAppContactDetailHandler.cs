using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContactDetail;

/// <summary>
/// A saved number's history, put together from the audit trail and from the row itself.
/// </summary>
/// <remarks>
/// <para>
/// Neither source is whole on its own. The audit trail has every save, change, opt-out and removal
/// made on the web, with who made it, but nothing from a van handset, a till, an invoice drawer's
/// "keep this number", a WhatsApp check or a STOP reply. The row keeps when it was created and the
/// latest of each of those, but a fresh save overwrites who recorded consent. So the audit rows come
/// first, and the row fills in what no audit row covers, each with who did it where that was kept.
/// </para>
/// <para>
/// "Added by" is the first save: the audit row nearest the row's creation, else the consent recorder
/// when consent was taken as the row was created. When neither holds, it is null rather than a guess.
/// </para>
/// </remarks>
public sealed class GetCustomerWhatsAppContactDetailHandler(ApplicationDbContext context)
    : IRequestHandler<GetCustomerWhatsAppContactDetailQuery, ErrorOr<CustomerWhatsAppContactDetailDto>>
{
    private const string EntityType = "CustomerWhatsAppContact";
    private const int RecentDeliveries = 5;

    /// <summary>How far apart a row's timestamp and an audit row's can be and still be one event.</summary>
    private static readonly TimeSpan SameEvent = TimeSpan.FromMinutes(2);

    public async Task<ErrorOr<CustomerWhatsAppContactDetailDto>> Handle(
        GetCustomerWhatsAppContactDetailQuery query,
        CancellationToken cancellationToken)
    {
        var row = await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .Where(contact => contact.Id == query.ContactId)
            .Select(contact => new
            {
                Contact = contact,
                RouteCustomerCode = contact.RouteCustomer == null ? null : contact.RouteCustomer.Code
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
            return Errors.CustomerDocuments.ContactNotFound(query.ContactId);

        var contact = row.Contact;
        var entityId = contact.Id.ToString();

        var audits = await context.AuditLogs
            .AsNoTracking()
            .Where(audit => audit.EntityType == EntityType && audit.EntityId == entityId && audit.IsSuccess)
            .OrderBy(audit => audit.Timestamp)
            .Select(audit => new AuditRow(audit.Timestamp, audit.Action, audit.Username, audit.Details))
            .ToListAsync(cancellationToken);

        var names = await FullNamesAsync(audits.Select(audit => audit.Username), cancellationToken);
        var history = Build(contact, audits, names);

        var deliveries = context.CustomerDocumentDeliveries
            .AsNoTracking()
            .Where(delivery => delivery.ContactId == contact.Id);

        var recent = await deliveries
            .OrderByDescending(delivery => delivery.CreatedAtUtc)
            .ThenByDescending(delivery => delivery.Id)
            .Take(RecentDeliveries)
            .Select(CustomerDocumentProjections.Delivery)
            .ToListAsync(cancellationToken);

        var sentCount = await deliveries.CountAsync(delivery =>
            delivery.Status == CustomerDocumentDeliveryStatus.Sent
            || delivery.Status == CustomerDocumentDeliveryStatus.SentUnconfirmed, cancellationToken);

        var waitingCount = await deliveries.CountAsync(delivery =>
            delivery.Status == CustomerDocumentDeliveryStatus.Pending
            || delivery.Status == CustomerDocumentDeliveryStatus.Preparing
            || delivery.Status == CustomerDocumentDeliveryStatus.WaitingForFiscal
            || delivery.Status == CustomerDocumentDeliveryStatus.Sending, cancellationToken);

        var lastSentAtUtc = await deliveries.MaxAsync(delivery => delivery.SentAtUtc, cancellationToken);

        var added = history.Where(entry => entry.Kind == "Saved").MinBy(entry => entry.AtUtc);

        return new CustomerWhatsAppContactDetailDto
        {
            Contact = CustomerDocumentProjections.Contact.Compile()(contact),
            RouteCustomerCode = row.RouteCustomerCode,
            AddedBy = added?.Who,
            AddedAtUtc = contact.CreatedAtUtc,
            AddedVia = added?.Via,
            SentCount = sentCount,
            WaitingCount = waitingCount,
            LastSentAtUtc = lastSentAtUtc,
            RecentDeliveries = recent,
            History = history.OrderByDescending(entry => entry.AtUtc).ToList()
        };
    }

    internal static List<CustomerWhatsAppContactEventDto> Build(
        CustomerWhatsAppContactEntity contact,
        IReadOnlyList<AuditRow> audits,
        IReadOnlyDictionary<string, string> names)
    {
        var history = new List<CustomerWhatsAppContactEventDto>();
        var owner = contact.OwnerName;
        var consentAtCreation = Near(contact.ConsentRecordedAtUtc, contact.CreatedAtUtc);

        foreach (var audit in audits)
        {
            var who = names.TryGetValue(audit.Username, out var name) ? name : audit.Username;
            var details = audit.Details ?? string.Empty;

            switch (audit.Action)
            {
                case AuditActions.SaveCustomerWhatsAppContact when details.StartsWith("Updated", StringComparison.Ordinal):
                    history.Add(Event(audit.Timestamp, "Changed",
                        details.Contains("automatic invoices on", StringComparison.Ordinal)
                            ? "Automatic invoices turned on"
                            : "Automatic invoices turned off",
                        who, "Web"));
                    break;

                case AuditActions.SaveCustomerWhatsAppContact:
                    var first = Near(audit.Timestamp, contact.CreatedAtUtc);
                    history.Add(Event(audit.Timestamp, "Saved",
                        (first ? $"Saved on {owner}" : $"Saved again on {owner}, with fresh agreement")
                        + (details.Contains("automatic invoices on", StringComparison.Ordinal) ? ", invoices sent automatically" : ", invoices on request"),
                        who, "Web"));
                    break;

                case AuditActions.OptOutCustomerWhatsAppContact:
                    history.Add(Event(audit.Timestamp, "OptedOut", "Opted out of every document, on every customer it is saved on", who, "Web"));
                    break;

                case AuditActions.RemoveCustomerWhatsAppContact:
                    history.Add(Event(audit.Timestamp, "Removed", $"Removed from {owner}", who, "Web"));
                    break;
            }
        }

        bool Covered(string kind, DateTime atUtc) =>
            history.Any(entry => entry.Kind == kind && Near(entry.AtUtc, atUtc));

        // The row's own creation, when no web save audited it: a van handset, a till, or an invoice
        // drawer's send that kept the number. Who is known only if consent was taken at that moment.
        if (!Covered("Saved", contact.CreatedAtUtc))
        {
            history.Add(Event(contact.CreatedAtUtc, "Saved", $"Saved on {owner}",
                consentAtCreation ? contact.ConsentRecordedBy : null,
                consentAtCreation ? contact.ConsentSource.ToString() : null));
        }

        // A later save with fresh consent that nothing audited.
        if (!consentAtCreation && !Covered("Saved", contact.ConsentRecordedAtUtc))
        {
            history.Add(Event(contact.ConsentRecordedAtUtc, "Saved", $"Saved again on {owner}, with fresh agreement",
                contact.ConsentRecordedBy, contact.ConsentSource.ToString()));
        }

        if (contact.OptedOutAtUtc is { } optedOutAt && !Covered("OptedOut", optedOutAt))
        {
            history.Add(contact.OptedOutSource == WhatsAppOptOutSource.StopKeyword
                ? Event(optedOutAt, "OptedOut", "The customer replied STOP: opted out of every document", null, "StopKeyword")
                : Event(optedOutAt, "OptedOut", "Opted out of every document", contact.OptedOutBy, "Web"));
        }

        if (contact.RemovedAtUtc is { } removedAt && !Covered("Removed", removedAt))
        {
            history.Add(Event(removedAt, "Removed", $"Removed from {owner}", contact.RemovedBy, null));
        }

        if (contact.WhatsAppCheckedAtUtc is { } checkedAt && contact.WhatsAppExists is { } exists)
        {
            history.Add(Event(checkedAt, "Checked",
                exists ? "WhatsApp check: the number has an account" : "WhatsApp check: no WhatsApp account",
                null, null));
        }

        return history;
    }

    private async Task<Dictionary<string, string>> FullNamesAsync(IEnumerable<string> usernames, CancellationToken cancellationToken)
    {
        var wanted = usernames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();
        if (wanted.Count == 0)
            return [];

        var users = await context.Users
            .AsNoTracking()
            .Where(user => wanted.Contains(user.Username))
            .Select(user => new { user.Username, user.FirstName, user.LastName })
            .ToListAsync(cancellationToken);

        return users
            .Select(user => (user.Username, Name: $"{user.FirstName} {user.LastName}".Trim()))
            .Where(user => user.Name.Length > 0)
            .ToDictionary(user => user.Username, user => user.Name, StringComparer.Ordinal);
    }

    private static bool Near(DateTime a, DateTime b) => (a - b).Duration() <= SameEvent;

    private static CustomerWhatsAppContactEventDto Event(DateTime atUtc, string kind, string what, string? who, string? via) =>
        new() { AtUtc = atUtc, Kind = kind, What = what, Who = string.IsNullOrWhiteSpace(who) ? null : who, Via = via };

    internal sealed record AuditRow(DateTime Timestamp, string Action, string Username, string? Details);
}
