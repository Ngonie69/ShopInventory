using System.Linq.Expressions;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// The register and the delivery log as their DTOs, written once for every query that reads them.
/// </summary>
/// <remarks>
/// Masking, the status names and the retry rules are plain methods, which EF runs on the columns it
/// fetched once the query returns — it reads only the columns named here.
/// </remarks>
internal static class CustomerDocumentProjections
{
    public static readonly Expression<Func<CustomerWhatsAppContactEntity, CustomerWhatsAppContactDto>> Contact =
        contact => new CustomerWhatsAppContactDto
        {
            Id = contact.Id,
            CardCode = contact.CardCode,
            RouteCustomerId = contact.RouteCustomerId,
            OwnerName = contact.OwnerName,
            PhoneE164 = contact.PhoneE164,
            PhoneMasked = WhatsAppRecipients.Mask(contact.PhoneE164),
            ContactName = contact.ContactName,
            AutoSendInvoices = contact.AutoSendInvoices,
            ConsentSource = contact.ConsentSource.ToString(),
            ConsentNote = contact.ConsentNote,
            ConsentRecordedAtUtc = contact.ConsentRecordedAtUtc,
            ConsentRecordedBy = contact.ConsentRecordedBy,
            IsOptedOut = contact.OptedOutAtUtc != null,
            OptedOutAtUtc = contact.OptedOutAtUtc,
            OptedOutSource = contact.OptedOutSource == null ? null : contact.OptedOutSource.ToString(),
            OptedOutBy = contact.OptedOutBy,
            WhatsAppExists = contact.WhatsAppExists,
            WhatsAppCheckedAtUtc = contact.WhatsAppCheckedAtUtc,
            IsRemoved = contact.RemovedAtUtc != null,
            CreatedAtUtc = contact.CreatedAtUtc,
            UpdatedAtUtc = contact.UpdatedAtUtc
        };

    public static readonly Expression<Func<CustomerDocumentDeliveryEntity, CustomerDocumentDeliveryDto>> Delivery =
        delivery => new CustomerDocumentDeliveryDto
        {
            Id = delivery.Id,
            DocumentType = delivery.DocumentType.ToString(),
            SapDocEntry = delivery.SapDocEntry,
            SapDocNum = delivery.SapDocNum,
            DesktopSaleId = delivery.DesktopSaleId,
            DocumentNumber = delivery.DocumentNumber,
            DocumentDate = delivery.DocumentDate,
            DocumentTotal = InvoiceDeliveryRules.DisplayTotalOrNull(delivery.DocumentTotal, delivery.DocumentTotalFc),
            Currency = delivery.Currency,
            CardCode = delivery.CardCode,
            CardName = delivery.CardName,
            RouteCustomerName = delivery.RouteCustomerName,
            ContactId = delivery.ContactId,
            RecipientMasked = WhatsAppRecipients.Mask(delivery.RecipientE164),
            RecipientName = delivery.RecipientName,
            Trigger = delivery.Trigger.ToString(),
            Status = delivery.Status.ToString(),
            StatusReason = delivery.StatusReason,
            LastError = delivery.LastError,
            RequestedBy = delivery.RequestedBy,
            DispatchAttempts = delivery.DispatchAttempts,
            NextAttemptAtUtc = delivery.NextAttemptAtUtc,
            CreatedAtUtc = delivery.CreatedAtUtc,
            SendIssuedAtUtc = delivery.SendIssuedAtUtc,
            SentAtUtc = delivery.SentAtUtc,
            ClosedAtUtc = delivery.ClosedAtUtc,
            ClosedBy = delivery.ClosedBy,
            FileName = delivery.FileName,
            FiscalEvidenceSource = delivery.FiscalEvidenceSource,
            SupersedesDeliveryId = delivery.SupersedesDeliveryId,
            CanRetry = CustomerDocumentDeliveryRules.CanRetry(delivery.Status),
            RetryNeedsConfirmation = CustomerDocumentDeliveryRules.RetryNeedsConfirmation(delivery.Status),
            CanCancel = CustomerDocumentDeliveryRules.CanCancel(delivery.Status)
        };
}
