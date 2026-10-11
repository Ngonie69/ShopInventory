using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.RequestVanSaleWhatsApp;

/// <summary>
/// Queues a van sale's invoice for the customer's WhatsApp: the number saved on the shop, or the one
/// the customer gives at the sale — which is then saved, so they are asked only once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked once per shop.</b> The handset asks with no number after every sale. A shop with a saved
/// number has its invoice queued for it there and then, and the rep is told where it went; a shop
/// without one answers <c>needs_number</c>, and the rep takes the number from the customer and reads
/// it back. That number is saved on the shop with the rep recorded as having taken the consent, and
/// marked for automatic invoices — so the next sale, on any handset, needs no question, and a sale
/// the handset never asks about (made offline, or raised at the office) is still sent by the invoice
/// scan. A number the office has set to "only when asked" is left as the office set it.
/// </para>
/// <para>
/// <b>Asked before the invoice exists.</b> The handset asks the moment the sale is made, and the office
/// usually posts it to SAP a few seconds later — an offline sale, at the next upload. So the delivery
/// names the sale, and the delivery job waits for the sale row to carry its SAP numbers before it
/// renders anything (see <c>SapInvoiceDocumentComposer</c>). From then on it is an ordinary invoice
/// send: the same fiscal check, pacing and caps, and the shop printed as the buyer, not the van.
/// </para>
/// <para>
/// <b>Whose sale.</b> The rep's own, or one billing a card in their scope — the second rep on the same
/// van. Anything else answers exactly as a sale that does not exist, as <c>GET sale/{vanOrder}</c> does.
/// </para>
/// <para>
/// <b>Safe to repeat.</b> One delivery per sale and number, whoever queued it — the handset, the office
/// or the scan — so a handset that lost the reply and asks again is handed the delivery already made.
/// </para>
/// </remarks>
public sealed class RequestVanSaleWhatsAppHandler(
    ApplicationDbContext context,
    IOptions<CustomerDocumentDeliverySettings> options,
    IOpenWAClient openWaClient,
    IOptions<OpenWASettings> openWaOptions,
    ICustomerDocumentDispatchTrigger dispatchTrigger,
    IAuditService auditService,
    ILogger<RequestVanSaleWhatsAppHandler> logger)
    : IRequestHandler<RequestVanSaleWhatsAppCommand, ErrorOr<VanSaleWhatsAppResponse>>
{
    /// <summary>The width of <see cref="DesktopSaleEntity.ExternalReferenceId"/>.</summary>
    private const int MaxVanOrderLength = 100;

    public async Task<ErrorOr<VanSaleWhatsAppResponse>> Handle(
        RequestVanSaleWhatsAppCommand command,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var request = command.Request;

        if (!settings.Enabled)
            return Errors.CustomerDocuments.Disabled;

        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);
        if (user is null || !user.IsActive)
            return Errors.CustomerDocuments.UserNotFound;

        // A rep cannot connect a number, so a gateway with none is not their error: the send is queued
        // and the office is told. Only sending an administrator stopped is said to the rep as it is.
        var runtime = await CustomerDocumentDeliveryKeys.ReadAsync(context, cancellationToken);
        var sending = await CustomerDocumentSession.EnsureAsync(
            context, openWaClient, openWaOptions.Value, settings, runtime, logger, cancellationToken);
        if (sending.Kind == SendingSessionKind.Stopped)
            return Errors.CustomerDocuments.SendingStopped;

        string? givenE164 = null;
        if (!string.IsNullOrWhiteSpace(request.Phone))
        {
            if (!WhatsAppRecipients.TryNormalise(request.Phone, settings.DefaultCountryCode, out givenE164))
                return Errors.CustomerDocuments.InvalidPhone(request.Phone);

            if (!request.Consent)
                return Errors.CustomerDocuments.ConsentRequired;
        }

        // Trimmed because the post trims it before writing the row.
        var vanOrder = command.VanOrder?.Trim() ?? string.Empty;
        if (vanOrder.Length == 0 || vanOrder.Length > MaxVanOrderLength)
            return Errors.CustomerDocuments.VanSaleNotFound(vanOrder);

        var sale = await context.DesktopSales
            .AsNoTracking()
            .Where(candidate => candidate.ExternalReferenceId == vanOrder
                && SaleSourceSystems.VanSaleSources.Contains(candidate.SourceSystem))
            .Select(candidate => new VanSale(
                candidate.Id,
                candidate.CardCode,
                candidate.CreatedBy,
                candidate.DocDate,
                candidate.TotalAmount,
                candidate.Currency,
                candidate.SapDocEntry,
                candidate.SapDocNum,
                candidate.RouteCustomerId,
                candidate.RouteCustomerCode,
                candidate.RouteCustomerName))
            .FirstOrDefaultAsync(cancellationToken);

        if (sale is null)
            return Errors.CustomerDocuments.VanSaleNotFound(vanOrder);

        var madeByCaller = Guid.TryParse(sale.CreatedBy, out var createdBy) && createdBy == user.Id;
        if (!madeByCaller)
        {
            var scope = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(context, user, logger, cancellationToken);
            if (!scope.Contains(sale.CardCode.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "User {UserId} asked to WhatsApp van sale {VanOrder}, which bills {CardCode} outside their scope; answered as not found",
                    user.Id, vanOrder, sale.CardCode);
                return Errors.CustomerDocuments.VanSaleNotFound(vanOrder);
            }
        }

        var actorName = await CustomerDocumentActor.ResolveNameAsync(context, user.Id, cancellationToken) ?? user.Username;
        var now = DateTime.UtcNow;

        return givenE164 is null
            ? await SendToSavedNumbersAsync(sale, vanOrder, user.Id, actorName, now, cancellationToken)
            : await SendToGivenNumberAsync(sale, vanOrder, givenE164, user.Id, actorName, now, cancellationToken);
    }

    // ── The shop's saved numbers ────────────────────────────────────────

    private async Task<ErrorOr<VanSaleWhatsAppResponse>> SendToSavedNumbersAsync(
        VanSale sale,
        string vanOrder,
        Guid userId,
        string actorName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var contacts = sale.RouteCustomerId is { } shopId
            ? await context.CustomerWhatsAppContacts
                .AsNoTracking()
                .Where(contact => contact.RouteCustomerId == shopId
                    && contact.RemovedAtUtc == null
                    && contact.OptedOutAtUtc == null
                    && contact.AutoSendInvoices)
                .OrderBy(contact => contact.Id)
                .ToListAsync(cancellationToken)
            : [];

        if (contacts.Count == 0)
        {
            return new VanSaleWhatsAppResponse
            {
                NeedsNumber = true,
                Message = "No WhatsApp number is saved for this customer yet. Ask for one and it is kept for their next invoices."
            };
        }

        var already = await FindExistingAsync(sale, cancellationToken);
        var queued = new List<CustomerDocumentDeliveryEntity>();
        var rows = new List<CustomerDocumentDeliveryEntity>();

        foreach (var contact in contacts)
        {
            if (already.FirstOrDefault(row => row.RecipientE164 == contact.PhoneE164) is { } existing)
            {
                rows.Add(existing);
                continue;
            }

            var delivery = NewDelivery(sale, vanOrder, contact.PhoneE164, userId, actorName, now);
            delivery.ContactId = contact.Id;
            delivery.RecipientName = CustomerDocumentDeliveryRules.Truncate(contact.ContactName ?? contact.OwnerName, 100);
            // Sent on the consent the number was saved under; nobody confirmed anything at this sale.
            delivery.ConsentAffirmed = false;
            queued.Add(delivery);
            rows.Add(delivery);
        }

        if (queued.Count > 0)
        {
            context.CustomerDocumentDeliveries.AddRange(queued);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (CustomerDocumentDbErrors.IsDuplicate(ex, "DesktopSaleId"))
            {
                // The same request racing its own retry: hand back what the one that won queued.
                context.ChangeTracker.Clear();
                var numbers = contacts.Select(contact => contact.PhoneE164).ToList();
                rows = (await FindExistingAsync(sale, cancellationToken))
                    .Where(row => numbers.Contains(row.RecipientE164))
                    .ToList();
                if (rows.Count == 0)
                    throw;
                queued.Clear();
            }

            foreach (var delivery in queued)
            {
                await AuditAsync(delivery, vanOrder, "the number saved for the customer");
            }

            if (queued.Count > 0)
                await dispatchTrigger.TriggerAsync(cancellationToken);
        }

        var masked = rows.Select(row => WhatsAppRecipients.Mask(row.RecipientE164)).Distinct().ToList();
        var list = string.Join(" and ", masked);
        return new VanSaleWhatsAppResponse
        {
            DeliveryId = rows[0].Id,
            Status = rows[0].Status.ToString(),
            Recipient = masked[0],
            Recipients = masked,
            Saved = true,
            AlreadyRequested = queued.Count == 0,
            Message = queued.Count == 0
                ? $"This invoice was already sent to {list}, or is on its way there."
                : $"The invoice is going to {list} on WhatsApp, the number saved for this customer."
        };
    }

    // ── A number given at the sale ──────────────────────────────────────

    private async Task<ErrorOr<VanSaleWhatsAppResponse>> SendToGivenNumberAsync(
        VanSale sale,
        string vanOrder,
        string phoneE164,
        Guid userId,
        string actorName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;

        var existing = (await FindExistingAsync(sale, cancellationToken))
            .FirstOrDefault(row => row.RecipientE164 == phoneE164);
        if (existing is not null)
            return Answer(existing, alreadyRequested: true, saved: existing.ContactId is not null);

        var optedOut = await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .AnyAsync(contact => contact.PhoneE164 == phoneE164
                && contact.OptedOutAtUtc != null
                && contact.RemovedAtUtc == null, cancellationToken);
        if (optedOut)
            return Errors.CustomerDocuments.NumberOptedOut(WhatsAppRecipients.Mask(phoneE164));

        // Numbers the rep typed and confirmed today. Sends to a shop's saved number are not counted:
        // they are one per sale by construction and need nobody's judgement.
        var dayStart = DeliveryBudget.CatDayStartUtc(now);
        var sentToday = await context.CustomerDocumentDeliveries
            .AsNoTracking()
            .CountAsync(delivery => delivery.RequestedByUserId == userId
                && delivery.Trigger == CustomerDocumentDeliveryTrigger.Counter
                && delivery.ConsentAffirmed
                && delivery.CreatedAtUtc >= dayStart, cancellationToken);
        if (sentToday >= settings.MaxVanSaleSendsPerUserPerDay)
            return Errors.CustomerDocuments.VanSaleSendLimitReached(settings.MaxVanSaleSendsPerUserPerDay);

        var contactId = await SaveOnShopAsync(sale, vanOrder, phoneE164, userId, actorName, now, cancellationToken);

        var delivery = NewDelivery(sale, vanOrder, phoneE164, userId, actorName, now);
        delivery.ContactId = contactId;
        delivery.ConsentAffirmed = true;

        context.CustomerDocumentDeliveries.Add(delivery);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (CustomerDocumentDbErrors.IsDuplicate(ex, "DesktopSaleId"))
        {
            // The same request racing its own retry: hand back the one that won.
            context.ChangeTracker.Clear();
            var winner = (await FindExistingAsync(sale, cancellationToken))
                .FirstOrDefault(row => row.RecipientE164 == phoneE164);
            if (winner is not null)
                return Answer(winner, alreadyRequested: true, saved: winner.ContactId is not null);
            throw;
        }

        await AuditAsync(delivery, vanOrder, contactId is null ? "a number given at the sale" : "a number given at the sale and saved on the customer");
        await dispatchTrigger.TriggerAsync(cancellationToken);

        return Answer(delivery, alreadyRequested: false, saved: contactId is not null);
    }

    /// <summary>
    /// Keeps the number on the shop, so its next invoices go without anyone asking again. Returns the
    /// contact, or null when the number could not be kept — the sale names no shop, the shop is gone
    /// or inactive, or it already has all the numbers it may. The invoice is sent either way.
    /// </summary>
    private async Task<int?> SaveOnShopAsync(
        VanSale sale,
        string vanOrder,
        string phoneE164,
        Guid userId,
        string actorName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (sale.RouteCustomerId is not { } shopId)
            return null;

        // Already on the shop: used as the office left it, automatic or only-when-asked.
        var saved = await FindContactIdAsync(shopId, phoneE164, cancellationToken);
        if (saved is not null)
            return saved;

        var shop = await context.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.Id == shopId)
            .Select(customer => new { customer.Name, customer.Surname, customer.IsActive })
            .FirstOrDefaultAsync(cancellationToken);
        if (shop is null || !shop.IsActive)
            return null;

        var staged = await CustomerWhatsAppContactWriter.StageAsync(
            context,
            new ContactOwner(null, shopId, $"{shop.Name} {shop.Surname}".Trim()),
            phoneE164,
            contactName: null,
            autoSendInvoices: true,
            WhatsAppConsentSource.VanHandset,
            $"Given at the van for sale {vanOrder}",
            userId,
            actorName,
            options.Value.MaxContactsPerOwner,
            now,
            cancellationToken);

        if (staged.IsError)
        {
            logger.LogInformation(
                "The number given for van sale {VanOrder} was not saved on route customer {RouteCustomerId}: {Reason}",
                vanOrder, shopId, staged.FirstError.Description);
            context.ChangeTracker.Clear();
            return null;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return staged.Value.Id;
        }
        catch (DbUpdateException ex) when (CustomerDocumentDbErrors.IsDuplicate(ex, "PhoneE164"))
        {
            // Saved by someone else a moment ago; theirs is the one to use.
            context.ChangeTracker.Clear();
            return await FindContactIdAsync(shopId, phoneE164, cancellationToken);
        }
    }

    private async Task<int?> FindContactIdAsync(int shopId, string phoneE164, CancellationToken cancellationToken) =>
        await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .Where(contact => contact.RouteCustomerId == shopId
                && contact.PhoneE164 == phoneE164
                && contact.RemovedAtUtc == null)
            .Select(contact => (int?)contact.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // ── Shared ──────────────────────────────────────────────────────────

    /// <summary>
    /// Everything already queued or sent for this sale, by any route: an earlier ask from a handset,
    /// the office pressing Send on the posted invoice, or the scan. The customer gets one copy.
    /// </summary>
    private async Task<List<CustomerDocumentDeliveryEntity>> FindExistingAsync(VanSale sale, CancellationToken cancellationToken)
    {
        var saleId = sale.Id;
        var docEntry = sale.SapDocEntry;

        return await context.CustomerDocumentDeliveries
            .AsNoTracking()
            .Where(delivery => delivery.DesktopSaleId == saleId
                || (docEntry != null && delivery.SapDocEntry == docEntry))
            .OrderBy(delivery => delivery.Id)
            .ToListAsync(cancellationToken);
    }

    private static CustomerDocumentDeliveryEntity NewDelivery(
        VanSale sale,
        string vanOrder,
        string phoneE164,
        Guid userId,
        string actorName,
        DateTime now)
    {
        var posted = sale.SapDocEntry is not null && sale.SapDocNum is not null;
        return new CustomerDocumentDeliveryEntity
        {
            DocumentType = CustomerDocumentType.SapInvoice,
            DesktopSaleId = sale.Id,
            SapDocEntry = posted ? sale.SapDocEntry : null,
            SapDocNum = posted ? sale.SapDocNum : null,
            // The slip's number until the office posts the sale; the SAP DocNum replaces it then.
            DocumentNumber = posted
                ? sale.SapDocNum!.Value.ToString(CultureInfo.InvariantCulture)
                : DesktopSaleNumber.Format(sale.Id),
            SaleReference = vanOrder,
            DocumentDate = DateTime.SpecifyKind(sale.DocDate.Date, DateTimeKind.Unspecified),
            DocumentTotal = sale.TotalAmount,
            Currency = sale.Currency,
            CardCode = sale.CardCode.Trim(),
            CardName = CustomerDocumentDeliveryRules.Truncate(sale.RouteCustomerName, 200),
            RouteCustomerId = sale.RouteCustomerId,
            RouteCustomerCode = sale.RouteCustomerCode,
            RouteCustomerName = sale.RouteCustomerName,
            RecipientE164 = phoneE164,
            Trigger = CustomerDocumentDeliveryTrigger.Counter,
            RequestedByUserId = userId,
            RequestedBy = actorName,
            Priority = 1,
            Status = CustomerDocumentDeliveryStatus.Pending,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private static VanSaleWhatsAppResponse Answer(CustomerDocumentDeliveryEntity delivery, bool alreadyRequested, bool saved)
    {
        var masked = WhatsAppRecipients.Mask(delivery.RecipientE164);
        return new VanSaleWhatsAppResponse
        {
            DeliveryId = delivery.Id,
            Status = delivery.Status.ToString(),
            Recipient = masked,
            Recipients = [masked],
            Saved = saved,
            AlreadyRequested = alreadyRequested,
            Message = alreadyRequested
                ? $"This invoice was already sent to {masked}, or is on its way there."
                : saved
                    ? $"The invoice will be sent to {masked} on WhatsApp once the office has it, usually within a few minutes. The number is saved: this customer's next invoices go to it without asking."
                    : $"The invoice will be sent to {masked} on WhatsApp once the office has it, usually within a few minutes."
        };
    }

    private async Task AuditAsync(CustomerDocumentDeliveryEntity delivery, string vanOrder, string numberSource)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.RequestDocumentWhatsApp,
                "CustomerDocumentDelivery",
                delivery.Id.ToString(CultureInfo.InvariantCulture),
                $"Asked at the van to send the invoice for sale {vanOrder} ({delivery.RouteCustomerName ?? delivery.CardCode}) to {WhatsAppRecipients.Mask(delivery.RecipientE164)} — {numberSource}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit WhatsApp delivery {DeliveryId}", delivery.Id);
        }
    }

    private sealed record VanSale(
        int Id,
        string CardCode,
        string? CreatedBy,
        DateTime DocDate,
        decimal TotalAmount,
        string Currency,
        int? SapDocEntry,
        int? SapDocNum,
        int? RouteCustomerId,
        string? RouteCustomerCode,
        string? RouteCustomerName);
}
