using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.RequestInvoiceWhatsApp;

/// <summary>
/// Queues an invoice for the customer's WhatsApp, to saved numbers or to one typed for this send.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is sent here. Each number becomes a delivery row and the delivery job is woken, so a send
/// asked for by a person keeps to the same pacing, the same fiscal check and the same duplicate guard
/// as every other — and still goes when the node that took the request has no gateway configured.
/// Nothing has to be set up first either: with no session saved, the gateway's ready one is chosen
/// here, and the send is refused only when the gateway was asked and has no number to send from.
/// </para>
/// <para>
/// The invoice is checked first, so the person is told at once rather than finding a held row later:
/// a cancelled invoice, a repost after the SAP update and an end-of-day consolidation are refused. A
/// saved number must be on the invoice's own customer and not opted out. A one-off number needs the
/// sender to confirm the customer asked for it there, and one-offs are capped per person per day —
/// they are where a typo or a misunderstanding sends a company document to a stranger. Asked to keep
/// the number, it is saved on the invoice's customer; on a till's or a van's own account, which is
/// nobody's to save a number on, the invoice still goes to it, for this send only.
/// </para>
/// </remarks>
public sealed class RequestInvoiceWhatsAppHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    IOptions<CustomerDocumentDeliverySettings> options,
    IOptions<FiscalisationSettings> fiscalisationOptions,
    IOpenWAClient openWaClient,
    IOptions<OpenWASettings> openWaOptions,
    ICustomerDocumentDispatchTrigger dispatchTrigger,
    IAuditService auditService,
    ILogger<RequestInvoiceWhatsAppHandler> logger)
    : IRequestHandler<RequestInvoiceWhatsAppCommand, ErrorOr<List<CustomerDocumentDeliveryDto>>>
{
    private static readonly Func<CustomerDocumentDeliveryEntity, CustomerDocumentDeliveryDto> ToDto =
        CustomerDocumentProjections.Delivery.Compile();

    public async Task<ErrorOr<List<CustomerDocumentDeliveryDto>>> Handle(
        RequestInvoiceWhatsAppCommand command,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var request = command.Request;

        if (!settings.Enabled)
            return Errors.CustomerDocuments.Disabled;

        if (!sapSettings.Value.Enabled)
            return Errors.CustomerDocuments.DocumentUnavailable("SAP is switched off, so no invoice can be read to send.");

        var actorName = await CustomerDocumentActor.ResolveNameAsync(context, command.UserId, cancellationToken);
        if (actorName is null)
            return Errors.CustomerDocuments.UserNotFound;

        var runtime = await CustomerDocumentDeliveryKeys.ReadAsync(context, cancellationToken);
        var sending = await CustomerDocumentSession.EnsureAsync(
            context, openWaClient, openWaOptions.Value, settings, runtime, logger, cancellationToken);
        if (sending.Refusal(settings.PreferredSessionName) is { } refusal)
            return refusal;

        Invoice? invoice;
        try
        {
            invoice = (await sapClient.GetInvoiceDeliveryHeadersAsync([command.DocEntry], cancellationToken))
                .FirstOrDefault(header => header.DocEntry == command.DocEntry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read invoice {DocEntry} from SAP to send it on WhatsApp", command.DocEntry);
            return Errors.CustomerDocuments.GatewayUnavailable("SAP could not be asked about the invoice. Try again in a moment.");
        }

        if (invoice is null)
            return Errors.CustomerDocuments.InvoiceNotFound(command.DocEntry);

        if (InvoiceDeliveryRules.IsCancelled(invoice))
            return Errors.CustomerDocuments.InvoiceCancelled(invoice.DocNum);

        if (InvoiceDeliveryRules.IsReposted(fiscalisationOptions.Value, invoice))
            return Errors.CustomerDocuments.RepostedNotSendable(invoice.DocNum, RepostedInvoiceMarker.OldInvoiceNumber(invoice.Comments));

        if (await InvoiceDeliveryRules.IsConsolidatedAsync(context, invoice, cancellationToken))
            return Errors.CustomerDocuments.ConsolidatedNotSendable(invoice.DocNum);

        // A van invoice is billed to the van's own card; the shop it was for is the customer here.
        var shop = (await RouteCustomerInvoiceResolver.ResolveAsync(context, [invoice], logger, cancellationToken))
            .GetValueOrDefault(invoice.DocEntry);

        var now = DateTime.UtcNow;
        var recipients = new List<Recipient>();

        var savedRecipients = await ResolveSavedContactsAsync(invoice, shop, request.ContactIds, cancellationToken);
        if (savedRecipients.IsError)
            return savedRecipients.Errors;
        recipients.AddRange(savedRecipients.Value);

        if (!string.IsNullOrWhiteSpace(request.OneOffPhone))
        {
            var oneOff = await ResolveOneOffAsync(invoice, shop, request, command.UserId, actorName, now, cancellationToken);
            if (oneOff.IsError)
                return oneOff.Errors;
            recipients.Add(oneOff.Value);
        }

        recipients = recipients
            .GroupBy(recipient => recipient.PhoneE164, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (recipients.Count == 0)
            return Errors.CustomerDocuments.RecipientRequired;

        var deliveries = recipients
            .Select(recipient => new CustomerDocumentDeliveryEntity
            {
                DocumentType = CustomerDocumentType.SapInvoice,
                SapDocEntry = invoice.DocEntry,
                SapDocNum = invoice.DocNum,
                DocumentNumber = invoice.DocNum.ToString(CultureInfo.InvariantCulture),
                SaleReference = Clean(invoice.U_Van_saleorder),
                DocumentDate = InvoiceDeliveryRules.ParseDocDate(invoice.DocDate),
                DocumentTotal = invoice.DocTotal,
                DocumentTotalFc = InvoiceDeliveryRules.ForeignTotal(invoice),
                Currency = Clean(invoice.DocCurrency),
                CardCode = Clean(invoice.CardCode),
                CardName = CustomerDocumentDeliveryRules.Truncate(shop?.RouteCustomerName ?? Clean(invoice.CardName), 200),
                RouteCustomerId = shop?.RouteCustomerId,
                RouteCustomerCode = shop?.RouteCustomerCode,
                RouteCustomerName = shop?.RouteCustomerName,
                ContactId = recipient.ContactId,
                RecipientE164 = recipient.PhoneE164,
                RecipientName = CustomerDocumentDeliveryRules.Truncate(recipient.Name, 100),
                Trigger = CustomerDocumentDeliveryTrigger.Manual,
                ConsentAffirmed = recipient.ConsentAffirmed,
                RequestedByUserId = command.UserId,
                RequestedBy = actorName,
                Priority = 1,
                Status = CustomerDocumentDeliveryStatus.Pending,
                NextAttemptAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            })
            .ToList();

        context.CustomerDocumentDeliveries.AddRange(deliveries);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var delivery in deliveries)
        {
            await AuditAsync(delivery);
        }

        await dispatchTrigger.TriggerAsync(cancellationToken);

        return deliveries.Select(ToDto).ToList();
    }

    private async Task<ErrorOr<List<Recipient>>> ResolveSavedContactsAsync(
        Invoice invoice,
        RouteCustomerInvoiceLink? shop,
        List<int>? contactIds,
        CancellationToken cancellationToken)
    {
        var ids = (contactIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
            return new List<Recipient>();

        var contacts = await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .Where(contact => ids.Contains(contact.Id) && contact.RemovedAtUtc == null)
            .ToListAsync(cancellationToken);

        var recipients = new List<Recipient>();
        foreach (var id in ids)
        {
            var contact = contacts.FirstOrDefault(row => row.Id == id);

            // A contact on another customer is refused as if it did not exist: the invoice is this
            // customer's, and a number saved for someone else did not agree to receive it. A van
            // invoice's customer is its shop, never the van's card.
            var ownsInvoice = contact is not null && (shop is not null
                ? contact.RouteCustomerId == shop.RouteCustomerId
                : contact.RouteCustomerId is null
                    && string.Equals(contact.CardCode?.Trim(), invoice.CardCode?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (contact is null || !ownsInvoice)
            {
                return Errors.CustomerDocuments.ContactNotFound(id);
            }

            if (contact.OptedOutAtUtc is not null)
                return Errors.CustomerDocuments.NumberOptedOut(WhatsAppRecipients.Mask(contact.PhoneE164));

            recipients.Add(new Recipient(contact.PhoneE164, contact.Id, contact.ContactName ?? contact.OwnerName, false));
        }

        return recipients;
    }

    private async Task<ErrorOr<Recipient>> ResolveOneOffAsync(
        Invoice invoice,
        RouteCustomerInvoiceLink? shop,
        RequestInvoiceWhatsAppRequest request,
        Guid userId,
        string actorName,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (!WhatsAppRecipients.TryNormalise(request.OneOffPhone, settings.DefaultCountryCode, out var phoneE164))
            return Errors.CustomerDocuments.InvalidPhone(request.OneOffPhone!);

        if (!request.ConsentAffirmed)
            return Errors.CustomerDocuments.ConsentRequired;

        var name = Clean(request.OneOffName);

        var owner = request.SaveAsContact
            ? await ResolveOwnerAsync(invoice, shop, cancellationToken)
            : (ErrorOr<ContactOwner>?)null;

        // A walk-in's invoice sits on the till's or the van's own card. The number cannot be kept
        // there — it would receive every invoice that account posts — but the customer in front of
        // the till still asked for this one, so it goes as a one-off.
        if (owner is { IsError: true } refused && refused.FirstError.Code != SellingAccountCode)
            return refused.Errors;

        if (owner is { IsError: false })
        {
            var staged = await CustomerWhatsAppContactWriter.StageAsync(
                context,
                owner.Value.Value,
                phoneE164,
                name,
                request.AutoSendFutureInvoices,
                WhatsAppConsentSource.Web,
                $"Given for invoice {invoice.DocNum}",
                userId,
                actorName,
                settings.MaxContactsPerOwner,
                nowUtc,
                cancellationToken);

            if (staged.IsError)
                return staged.Errors;

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (CustomerDocumentDbErrors.IsDuplicate(ex, "PhoneE164"))
            {
                return Error.Conflict(
                    "CustomerDocuments.ContactSavedConcurrently",
                    "This number was saved on the customer by someone else a moment ago. Refresh and try again.");
            }

            return new Recipient(phoneE164, staged.Value.Id, name ?? staged.Value.OwnerName, true);
        }

        var optedOut = await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .AnyAsync(contact => contact.PhoneE164 == phoneE164
                && contact.OptedOutAtUtc != null
                && contact.RemovedAtUtc == null, cancellationToken);

        if (optedOut)
            return Errors.CustomerDocuments.NumberOptedOut(WhatsAppRecipients.Mask(phoneE164));

        var dayStart = DeliveryBudget.CatDayStartUtc(nowUtc);
        var oneOffsToday = await context.CustomerDocumentDeliveries
            .AsNoTracking()
            .CountAsync(delivery => delivery.RequestedByUserId == userId
                && delivery.ContactId == null
                && delivery.CreatedAtUtc >= dayStart, cancellationToken);

        if (oneOffsToday >= settings.MaxOneOffPerUserPerDay)
            return Errors.CustomerDocuments.OneOffLimitReached(settings.MaxOneOffPerUserPerDay);

        return new Recipient(phoneE164, null, name, true);
    }

    /// <summary>
    /// Who a number typed for an invoice is saved on: the shop for a van invoice, the card otherwise —
    /// and never a selling account's card, whose invoices belong to every buyer.
    /// </summary>
    private async Task<ErrorOr<ContactOwner>> ResolveOwnerAsync(
        Invoice invoice,
        RouteCustomerInvoiceLink? shop,
        CancellationToken cancellationToken)
    {
        if (shop is not null)
        {
            var routeCustomer = await context.RouteCustomers
                .AsNoTracking()
                .Where(customer => customer.Id == shop.RouteCustomerId)
                .Select(customer => new { customer.Name, customer.Surname, customer.IsActive })
                .FirstOrDefaultAsync(cancellationToken);

            if (routeCustomer is null)
                return Errors.CustomerDocuments.RouteCustomerNotFound(shop.RouteCustomerId);

            if (!routeCustomer.IsActive)
                return Errors.CustomerDocuments.RouteCustomerInactive(shop.RouteCustomerId);

            return new ContactOwner(null, shop.RouteCustomerId, $"{routeCustomer.Name} {routeCustomer.Surname}".Trim());
        }

        var cardCode = Clean(invoice.CardCode);
        if (cardCode is null)
            return Errors.CustomerDocuments.OwnerRequired;

        if (await SellingAccountCards.IsSellingAccountAsync(context, cardCode, cancellationToken))
            return Errors.CustomerDocuments.SellingAccountNotAllowed(cardCode);

        return new ContactOwner(cardCode, null, Clean(invoice.CardName) ?? cardCode);
    }

    private async Task AuditAsync(CustomerDocumentDeliveryEntity delivery)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.RequestDocumentWhatsApp,
                "CustomerDocumentDelivery",
                delivery.Id.ToString(CultureInfo.InvariantCulture),
                $"Asked to send invoice {delivery.DocumentNumber} ({delivery.RouteCustomerName ?? delivery.CardCode}) to {WhatsAppRecipients.Mask(delivery.RecipientE164)}"
                + (delivery.ContactId is null ? " — a number typed for this send" : string.Empty),
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit WhatsApp delivery {DeliveryId}", delivery.Id);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly string SellingAccountCode = Errors.CustomerDocuments.SellingAccountNotAllowed(string.Empty).Code;

    private sealed record Recipient(string PhoneE164, int? ContactId, string? Name, bool ConsentAffirmed);
}
