using System.Globalization;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes.Queries.GetCreditNoteReasons;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;

namespace ShopInventory.Features.Invoices.Commands.CancelInvoice;

/// <summary>
/// The stored, bell-visible notification that an invoice was cancelled.
/// </summary>
/// <remarks>
/// Separate from the SignalR push that tells the till. That push is addressed to one shop and is
/// gone if nothing is listening; this is the record everyone who works invoices sees afterwards, in
/// the panel and on a page reload, and it is what a cashier who was not at the screen at the time
/// finds later.
/// </remarks>
internal static class InvoiceCancellationNotificationFactory
{
    private const string ActionUrl = "/credit-notes";

    public static CreateNotificationRequest Create(
        Invoice invoice,
        CreditNoteDto creditNote,
        CreditNoteReasonOption reason,
        string? comments)
    {
        var customer = ModuleNotificationFactory.DescribeBusinessPartner(invoice.CardCode, invoice.CardName);
        var amount = ModuleNotificationFactory.DescribeMoney(invoice.DocCurrency, invoice.DocTotal);

        var message =
            $"Invoice {invoice.DocNum} for {customer} totaling {amount} was cancelled ({reason.Description}). " +
            $"Credit note {creditNote.CreditNoteNumber} reverses it." +
            (string.IsNullOrWhiteSpace(comments) ? string.Empty : $" {comments.Trim()}");

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["invoiceDocEntry"] = invoice.DocEntry.ToString(CultureInfo.InvariantCulture),
            ["invoiceDocNum"] = invoice.DocNum.ToString(CultureInfo.InvariantCulture),
            ["cardCode"] = invoice.CardCode ?? string.Empty,
            ["cardName"] = invoice.CardName ?? string.Empty,
            ["currency"] = invoice.DocCurrency ?? string.Empty,
            // Invariant and ungrouped: a value to be parsed, not a figure to be printed. The
            // readable amount is already in the message.
            ["docTotal"] = invoice.DocTotal.ToString(CultureInfo.InvariantCulture),
            ["creditNoteId"] = creditNote.Id.ToString(CultureInfo.InvariantCulture),
            ["creditNoteNumber"] = creditNote.CreditNoteNumber,
            ["creditNoteDocNum"] = creditNote.SAPDocNum?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["reason"] = reason.Value,
            ["reasonDescription"] = reason.Description
        };

        return ModuleNotificationFactory.CreateBroadcastNotification(
            $"Invoice Cancelled: {invoice.DocNum}",
            message,
            "Warning",
            "CreditNote",
            "Invoice",
            invoice.DocEntry.ToString(CultureInfo.InvariantCulture),
            ActionUrl,
            payload);
    }
}
