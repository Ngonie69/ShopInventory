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
/// Queues a van sale's invoice for the WhatsApp number the customer gave at the sale.
/// </summary>
/// <remarks>
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
/// <b>Safe to repeat.</b> One delivery per sale and number, held by the unique index on counter sends,
/// so a handset that lost the reply and asks again is handed the delivery it already made. The number
/// is kept on that delivery only: it is not saved on the shop, which needs the consent office staff
/// record on the Web.
/// </para>
/// </remarks>
public sealed class RequestVanSaleWhatsAppHandler(
    ApplicationDbContext context,
    IOptions<CustomerDocumentDeliverySettings> options,
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

        var runtime = await CustomerDocumentDeliveryKeys.ReadAsync(context, cancellationToken);
        if (runtime.WhatsAppSessionId is null)
            return Errors.CustomerDocuments.SessionNotConfigured;

        if (!WhatsAppRecipients.TryNormalise(request.Phone, settings.DefaultCountryCode, out var phoneE164))
            return Errors.CustomerDocuments.InvalidPhone(request.Phone ?? string.Empty);

        if (!request.Consent)
            return Errors.CustomerDocuments.ConsentRequired;

        // Trimmed because the post trims it before writing the row.
        var vanOrder = command.VanOrder?.Trim() ?? string.Empty;
        if (vanOrder.Length == 0 || vanOrder.Length > MaxVanOrderLength)
            return Errors.CustomerDocuments.VanSaleNotFound(vanOrder);

        var sale = await context.DesktopSales
            .AsNoTracking()
            .Where(candidate => candidate.ExternalReferenceId == vanOrder
                && SaleSourceSystems.VanSaleSources.Contains(candidate.SourceSystem))
            .Select(candidate => new
            {
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
                candidate.RouteCustomerName
            })
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

        var existing = await FindExistingAsync(sale.Id, phoneE164, cancellationToken);
        if (existing is not null)
            return Answer(existing, alreadyRequested: true);

        var optedOut = await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .AnyAsync(contact => contact.PhoneE164 == phoneE164
                && contact.OptedOutAtUtc != null
                && contact.RemovedAtUtc == null, cancellationToken);
        if (optedOut)
            return Errors.CustomerDocuments.NumberOptedOut(WhatsAppRecipients.Mask(phoneE164));

        var now = DateTime.UtcNow;
        var dayStart = DeliveryBudget.CatDayStartUtc(now);
        var sentToday = await context.CustomerDocumentDeliveries
            .AsNoTracking()
            .CountAsync(delivery => delivery.RequestedByUserId == user.Id
                && delivery.Trigger == CustomerDocumentDeliveryTrigger.Counter
                && delivery.CreatedAtUtc >= dayStart, cancellationToken);
        if (sentToday >= settings.MaxVanSaleSendsPerUserPerDay)
            return Errors.CustomerDocuments.VanSaleSendLimitReached(settings.MaxVanSaleSendsPerUserPerDay);

        var posted = sale.SapDocEntry is not null && sale.SapDocNum is not null;
        var actorName = await CustomerDocumentActor.ResolveNameAsync(context, user.Id, cancellationToken) ?? user.Username;
        var delivery = new CustomerDocumentDeliveryEntity
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
            ConsentAffirmed = true,
            RequestedByUserId = user.Id,
            RequestedBy = actorName,
            Priority = 1,
            Status = CustomerDocumentDeliveryStatus.Pending,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        context.CustomerDocumentDeliveries.Add(delivery);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (CustomerDocumentDbErrors.IsDuplicate(ex, "DesktopSaleId"))
        {
            // The same request racing its own retry: hand back the one that won.
            context.ChangeTracker.Clear();
            var winner = await FindExistingAsync(sale.Id, phoneE164, cancellationToken);
            if (winner is not null)
                return Answer(winner, alreadyRequested: true);
            throw;
        }

        await AuditAsync(delivery, vanOrder);
        await dispatchTrigger.TriggerAsync(cancellationToken);

        return Answer(delivery, alreadyRequested: false);
    }

    private Task<CustomerDocumentDeliveryEntity?> FindExistingAsync(int saleId, string phoneE164, CancellationToken cancellationToken) =>
        context.CustomerDocumentDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(delivery => delivery.DesktopSaleId == saleId
                && delivery.RecipientE164 == phoneE164
                && delivery.Trigger == CustomerDocumentDeliveryTrigger.Counter, cancellationToken);

    private static VanSaleWhatsAppResponse Answer(CustomerDocumentDeliveryEntity delivery, bool alreadyRequested)
    {
        var masked = WhatsAppRecipients.Mask(delivery.RecipientE164);
        return new VanSaleWhatsAppResponse
        {
            DeliveryId = delivery.Id,
            Status = delivery.Status.ToString(),
            Recipient = masked,
            AlreadyRequested = alreadyRequested,
            Message = alreadyRequested
                ? $"This invoice was already sent to {masked}, or is on its way there."
                : $"The invoice will be sent to {masked} on WhatsApp once the office has it, usually within a few minutes."
        };
    }

    private async Task AuditAsync(CustomerDocumentDeliveryEntity delivery, string vanOrder)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.RequestDocumentWhatsApp,
                "CustomerDocumentDelivery",
                delivery.Id.ToString(CultureInfo.InvariantCulture),
                $"Asked at the van to send the invoice for sale {vanOrder} ({delivery.RouteCustomerName ?? delivery.CardCode}) to {WhatsAppRecipients.Mask(delivery.RecipientE164)}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit WhatsApp delivery {DeliveryId}", delivery.Id);
        }
    }
}
