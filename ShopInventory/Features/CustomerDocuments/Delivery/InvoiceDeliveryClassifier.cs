using ShopInventory.Configuration;
using ShopInventory.Models;

namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>What the invoice scan does with one new invoice of a customer who asked for theirs.</summary>
public enum InvoiceDeliveryDecisionKind
{
    /// <summary>Not a customer's invoice to receive at all; nothing is written.</summary>
    Ignore = 0,

    /// <summary>The customer's invoice, deliberately not sent; a Skipped row says why.</summary>
    Skip = 1,

    /// <summary>Queue it for each of the customer's automatic numbers.</summary>
    Queue = 2
}

public sealed record InvoiceDeliveryDecision(InvoiceDeliveryDecisionKind Kind, string? Reason = null)
{
    public static readonly InvoiceDeliveryDecision Queue = new(InvoiceDeliveryDecisionKind.Queue);

    public static InvoiceDeliveryDecision Ignore(string reason) => new(InvoiceDeliveryDecisionKind.Ignore, reason);

    public static InvoiceDeliveryDecision Skip(string reason) => new(InvoiceDeliveryDecisionKind.Skip, reason);
}

/// <summary>
/// Decides, for a new SAP invoice whose card has numbers marked for automatic invoices, whether it is
/// sent. Kept free of the database and SAP so every case is a plain test.
/// </summary>
/// <remarks>
/// <para>
/// Ignored, with no row: a cancelled invoice and the document that cancels one, an end-of-day
/// consolidation, and anything on a selling account. A till, a van and a cart vendor invoice every
/// buyer to their own card, so a number can never rightly sit on one — and if it somehow does, the
/// numbers on it must not be sent every walk-in's invoice.
/// </para>
/// <para>
/// Skipped, with a row, because the customer asked for their invoices and someone may later wonder
/// why this one did not come: an invoice reposted after the SAP update, whose receipt is filed under its
/// old number, and one dated too far back to be news.
/// </para>
/// </remarks>
public static class InvoiceDeliveryClassifier
{
    public static InvoiceDeliveryDecision Classify(
        Invoice invoice,
        bool isConsolidated,
        bool isSellingAccount,
        bool isReposted,
        DateTime todayCat,
        CustomerDocumentDeliverySettings settings)
    {
        if (InvoiceDeliveryRules.IsCancelled(invoice))
            return InvoiceDeliveryDecision.Ignore("cancelled");

        if (isSellingAccount)
            return InvoiceDeliveryDecision.Ignore("selling account");

        if (isConsolidated)
            return InvoiceDeliveryDecision.Ignore("consolidated");

        if (isReposted)
            return InvoiceDeliveryDecision.Skip(
                "Reposted after the SAP update. Its receipt is filed under the old invoice number, so it is not sent automatically.");

        var maxAge = Math.Max(0, settings.AutoMaxDocumentAgeDays);
        if (InvoiceDeliveryRules.ParseDocDate(invoice.DocDate) is { } docDate && docDate.Date < todayCat.Date.AddDays(-maxAge))
            return InvoiceDeliveryDecision.Skip(
                $"Dated {docDate:dd MMM yyyy}, more than {maxAge} days before it reached SAP, so it is not sent automatically. Send it from the invoice if the customer still needs it.");

        return InvoiceDeliveryDecision.Queue;
    }
}
