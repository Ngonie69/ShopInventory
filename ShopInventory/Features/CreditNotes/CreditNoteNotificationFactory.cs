using System.Globalization;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CreditNotes;

/// <summary>
/// Builds the credit note lifecycle notifications.
/// </summary>
/// <remarks>
/// The "CreditNote" category has always been part of <c>InvoiceBroadcastCategories</c> and
/// <c>/credit-notes</c> has always had an audience rule, but nothing in the module ever raised a
/// notification — a credit note could be created, approved or cancelled without anyone being told.
/// Both the category and the route resolve to the invoice audience (Admin, Cashier, Sales), which
/// is who works these documents.
/// </remarks>
internal static class CreditNoteNotificationFactory
{
    private const string ActionUrl = "/credit-notes";

    public static CreateNotificationRequest CreateCreatedNotification(CreditNoteDto creditNote) =>
        CreateNotification(
            creditNote,
            $"Credit Note Created: {creditNote.CreditNoteNumber}",
            $"Credit note {creditNote.CreditNoteNumber} for " +
            $"{ModuleNotificationFactory.DescribeBusinessPartner(creditNote.CardCode, creditNote.CardName)} " +
            $"totaling {ModuleNotificationFactory.DescribeMoney(creditNote.Currency, creditNote.DocTotal)} was created" +
            DescribeReason(creditNote.Reason) + ".",
            "Success");

    public static CreateNotificationRequest CreateApprovedNotification(CreditNoteDto creditNote) =>
        CreateNotification(
            creditNote,
            $"Credit Note Approved: {creditNote.CreditNoteNumber}",
            $"Credit note {creditNote.CreditNoteNumber} for " +
            $"{ModuleNotificationFactory.DescribeBusinessPartner(creditNote.CardCode, creditNote.CardName)} " +
            $"totaling {ModuleNotificationFactory.DescribeMoney(creditNote.Currency, creditNote.DocTotal)} was approved.",
            "Success");

    public static CreateNotificationRequest CreateStatusChangedNotification(CreditNoteDto creditNote) =>
        CreateNotification(
            creditNote,
            $"Credit Note {creditNote.StatusName}: {creditNote.CreditNoteNumber}",
            $"Credit note {creditNote.CreditNoteNumber} for " +
            $"{ModuleNotificationFactory.DescribeBusinessPartner(creditNote.CardCode, creditNote.CardName)} " +
            $"is now {creditNote.StatusName}.",
            GetTypeForStatus(creditNote.Status));

    private static CreateNotificationRequest CreateNotification(
        CreditNoteDto creditNote,
        string title,
        string message,
        string type)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["creditNoteId"] = creditNote.Id.ToString(),
            ["creditNoteNumber"] = creditNote.CreditNoteNumber,
            ["sapDocEntry"] = creditNote.SAPDocEntry?.ToString() ?? string.Empty,
            ["sapDocNum"] = creditNote.SAPDocNum?.ToString() ?? string.Empty,
            ["cardCode"] = creditNote.CardCode,
            ["cardName"] = creditNote.CardName ?? string.Empty,
            ["currency"] = creditNote.Currency ?? string.Empty,
            // Invariant and ungrouped: a value to be parsed, not a figure to be printed. The
            // readable amount is already in the message.
            ["docTotal"] = creditNote.DocTotal.ToString(CultureInfo.InvariantCulture),
            ["status"] = creditNote.StatusName,
            ["type"] = creditNote.TypeName,
            ["reason"] = creditNote.Reason ?? string.Empty,
            ["originalInvoiceDocNum"] = creditNote.OriginalInvoiceSAPDocNum?.ToString() ?? string.Empty
        };

        return ModuleNotificationFactory.CreateBroadcastNotification(
            title,
            message,
            type,
            "CreditNote",
            "CreditNote",
            creditNote.SAPDocEntry?.ToString() ?? creditNote.Id.ToString(),
            ActionUrl,
            payload);
    }

    private static string DescribeReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason.Trim()})";

    private static string GetTypeForStatus(CreditNoteStatus status)
        => status switch
        {
            CreditNoteStatus.Cancelled => "Warning",
            CreditNoteStatus.Draft => "Info",
            CreditNoteStatus.Pending => "Info",
            _ => "Success"
        };
}
