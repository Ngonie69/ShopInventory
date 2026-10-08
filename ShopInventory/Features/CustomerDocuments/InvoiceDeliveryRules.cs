using System.Globalization;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// Which SAP invoices may be sent to a customer at all, and the facts about one a delivery keeps.
/// </summary>
/// <remarks>
/// Asked twice: when someone asks for a send, so they are told at once, and again just before the
/// document goes, because an invoice can be cancelled in SAP while its delivery waits for its receipt.
/// </remarks>
internal static class InvoiceDeliveryRules
{
    /// <summary>Cancelled, or the document that cancels another.</summary>
    public static bool IsCancelled(Invoice invoice) =>
        string.Equals(invoice.Cancelled, "tYES", StringComparison.OrdinalIgnoreCase)
        || string.Equals(invoice.CancelStatus, "csYes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(invoice.CancelStatus, "csCancellation", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An end-of-day invoice covering many sales. Each buyer already holds their own receipt, and the
    /// consolidated document names none of them, so it is never a customer's invoice to receive.
    /// </summary>
    /// <remarks>
    /// The <c>CONSOL-</c> reference decides it for every consolidation since the reference existed.
    /// The registry is asked only about an invoice with no reference at all — the older ones — and
    /// only believed for the same customer, because it is keyed on a DocNum, and SAP has reissued
    /// DocNums since.
    /// </remarks>
    public static async Task<bool> IsConsolidatedAsync(
        ApplicationDbContext context,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        if (invoice.U_Van_saleorder?.Trim().StartsWith(SaleReferenceNamespace.ConsolidationPrefix, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        if (!string.IsNullOrWhiteSpace(invoice.U_Van_saleorder))
            return false;

        var consolidation = await ConsolidatedInvoiceRegistry.FindByDocNumAsync(context, invoice.DocNum, cancellationToken);
        return consolidation is not null
            && string.Equals(consolidation.CardCode?.Trim(), invoice.CardCode?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reposted after the SAP update: its receipt is under its old number, and it must never be filed again.</summary>
    public static bool IsReposted(FiscalisationSettings settings, Invoice invoice) =>
        RepostedInvoiceMarker.IsReposted(settings, invoice.Comments);

    /// <summary>The total in the document's own currency, when that is a foreign one; null otherwise.</summary>
    public static decimal? ForeignTotal(Invoice invoice) =>
        invoice.DocTotalFc != 0m ? invoice.DocTotalFc : null;

    /// <summary>The total the customer reads on the invoice.</summary>
    public static decimal DisplayTotal(decimal? localTotal, decimal? foreignTotal) =>
        DisplayTotalOrNull(localTotal, foreignTotal) ?? 0m;

    /// <inheritdoc cref="DisplayTotal"/>
    public static decimal? DisplayTotalOrNull(decimal? localTotal, decimal? foreignTotal) =>
        foreignTotal is { } foreign && foreign != 0m ? foreign : localTotal;

    /// <summary>SAP's DocDate as the calendar day it names.</summary>
    public static DateTime? ParseDocDate(string? docDate)
    {
        if (string.IsNullOrWhiteSpace(docDate))
            return null;

        return DateTime.TryParse(
            docDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
            out var parsed)
            ? DateTime.SpecifyKind(parsed.Date, DateTimeKind.Unspecified)
            : null;
    }
}
