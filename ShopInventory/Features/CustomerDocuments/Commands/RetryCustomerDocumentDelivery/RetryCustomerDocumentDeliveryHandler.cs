using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.RetryCustomerDocumentDelivery;

/// <summary>
/// Sends a document again. Always a new manual delivery pointing back at the one it replaces, so the
/// history keeps both — never the old row put back in the queue.
/// </summary>
/// <remarks>
/// <para>
/// A delivery the job is still working on cannot be retried: that would race it. One that may well
/// have reached the customer — sent, sent without confirmation, or uncertain — needs the sender to say
/// the customer did not get it, because a resend is then a deliberate second copy.
/// </para>
/// <para>
/// A held or uncertain row the person has now acted on is closed as replaced, so it stops asking for
/// attention and the job stops trying to settle it.
/// </para>
/// </remarks>
public sealed class RetryCustomerDocumentDeliveryHandler(
    ApplicationDbContext context,
    IOptions<CustomerDocumentDeliverySettings> options,
    ICustomerDocumentDispatchTrigger dispatchTrigger,
    IAuditService auditService,
    ILogger<RetryCustomerDocumentDeliveryHandler> logger)
    : IRequestHandler<RetryCustomerDocumentDeliveryCommand, ErrorOr<CustomerDocumentDeliveryDto>>
{
    private static readonly Func<CustomerDocumentDeliveryEntity, CustomerDocumentDeliveryDto> ToDto =
        CustomerDocumentProjections.Delivery.Compile();

    public async Task<ErrorOr<CustomerDocumentDeliveryDto>> Handle(
        RetryCustomerDocumentDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return Errors.CustomerDocuments.Disabled;

        var actorName = await CustomerDocumentActor.ResolveNameAsync(context, command.UserId, cancellationToken);
        if (actorName is null)
            return Errors.CustomerDocuments.UserNotFound;

        var original = await context.CustomerDocumentDeliveries
            .AsTracking()
            .FirstOrDefaultAsync(delivery => delivery.Id == command.DeliveryId, cancellationToken);

        if (original is null)
            return Errors.CustomerDocuments.DeliveryNotFound(command.DeliveryId);

        if (!CustomerDocumentDeliveryRules.CanRetry(original.Status))
            return Errors.CustomerDocuments.DeliveryNotRetryable(original.Status.ToString());

        if (CustomerDocumentDeliveryRules.RetryNeedsConfirmation(original.Status) && !command.Request.ConfirmNotReceived)
            return Errors.CustomerDocuments.ReceiptNotConfirmed;

        var optedOut = original.ContactId is { } contactId
            ? await context.CustomerWhatsAppContacts.AsNoTracking()
                .AnyAsync(contact => contact.Id == contactId && (contact.OptedOutAtUtc != null || contact.RemovedAtUtc != null), cancellationToken)
            : await context.CustomerWhatsAppContacts.AsNoTracking()
                .AnyAsync(contact => contact.PhoneE164 == original.RecipientE164 && contact.OptedOutAtUtc != null && contact.RemovedAtUtc == null, cancellationToken);

        if (optedOut)
            return Errors.CustomerDocuments.NumberOptedOut(WhatsAppRecipients.Mask(original.RecipientE164));

        var now = DateTime.UtcNow;
        var originalStatus = original.Status;
        var resend = new CustomerDocumentDeliveryEntity
        {
            DocumentType = original.DocumentType,
            SapDocEntry = original.SapDocEntry,
            SapDocNum = original.SapDocNum,
            DesktopSaleId = original.DesktopSaleId,
            DocumentNumber = original.DocumentNumber,
            SaleReference = original.SaleReference,
            DocumentDate = original.DocumentDate,
            DocumentTotal = original.DocumentTotal,
            DocumentTotalFc = original.DocumentTotalFc,
            Currency = original.Currency,
            CardCode = original.CardCode,
            CardName = original.CardName,
            RouteCustomerId = original.RouteCustomerId,
            RouteCustomerCode = original.RouteCustomerCode,
            RouteCustomerName = original.RouteCustomerName,
            ContactId = original.ContactId,
            RecipientE164 = original.RecipientE164,
            RecipientName = original.RecipientName,
            // A resend is a person's decision, whatever started the first: it is not held to the
            // automatic window or cap, and it never collides with the one automatic row per invoice.
            Trigger = CustomerDocumentDeliveryTrigger.Manual,
            ConsentAffirmed = original.ConsentAffirmed,
            RequestedByUserId = command.UserId,
            RequestedBy = actorName,
            Priority = 1,
            Status = CustomerDocumentDeliveryStatus.Pending,
            NextAttemptAtUtc = now,
            SupersedesDeliveryId = original.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        context.CustomerDocumentDeliveries.Add(resend);

        if (original.Status is CustomerDocumentDeliveryStatus.Held or CustomerDocumentDeliveryStatus.Uncertain)
        {
            original.Status = CustomerDocumentDeliveryStatus.Cancelled;
            original.StatusReason = $"Replaced by a resend by {actorName}.";
            original.ClosedAtUtc = now;
            original.ClosedBy = actorName;
            original.UpdatedAtUtc = now;
        }

        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.RetryDocumentWhatsApp,
                "CustomerDocumentDelivery",
                resend.Id.ToString(CultureInfo.InvariantCulture),
                $"Resent document {resend.DocumentNumber} to {WhatsAppRecipients.Mask(resend.RecipientE164)}, replacing delivery {original.Id} ({originalStatus})"
                + (command.Request.ConfirmNotReceived ? "; sender confirmed the customer did not receive it" : string.Empty),
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the resend of WhatsApp delivery {DeliveryId}", original.Id);
        }

        await dispatchTrigger.TriggerAsync(cancellationToken);

        return ToDto(resend);
    }
}
