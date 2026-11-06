using ShopInventory.Common.Sales;
using ShopInventory.Models;

namespace ShopInventory.Features.CreditNotes;

/// <summary>
/// What a SAP credit memo states, in the memo's own currency, for the documents a customer reads.
/// </summary>
/// <remarks>
/// SAP's plain totals are in the company's local currency (USD) on every document, so a ZiG memo read
/// from them shows its USD value under a ZiG label. Every amount here goes through
/// <see cref="SapDocumentAmounts"/>, which picks the document's own currency.
/// </remarks>
internal static class CreditNoteFigures
{
    private const int ArInvoiceObjectType = 13;

    /// <summary>
    /// A memo that was cancelled, or the document that cancelled one. Neither is a credit the
    /// customer holds: the pair cancel out, and listing either would state a credit that is not there.
    /// </summary>
    public static bool IsCancelledOrCancellation(SAPCreditNote creditNote) =>
        string.Equals(creditNote.Cancelled, "tYES", StringComparison.OrdinalIgnoreCase)
        || string.Equals(creditNote.CancelStatus, "csYes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(creditNote.CancelStatus, "csCancellation", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether part of the memo is still to be used. SAP closes a memo once it is used up.</summary>
    public static bool IsOpen(SAPCreditNote creditNote) =>
        string.Equals(creditNote.DocumentStatus, "bost_Open", StringComparison.OrdinalIgnoreCase);

    public static string Currency(SAPCreditNote creditNote) =>
        string.IsNullOrWhiteSpace(creditNote.DocCurrency) ? "USD" : creditNote.DocCurrency.Trim();

    /// <summary>The memo's total, VAT included, in its own currency.</summary>
    public static decimal Total(SAPCreditNote creditNote) =>
        SapDocumentAmounts.DocTotal(creditNote) ?? creditNote.DocTotal;

    /// <summary>The VAT the memo gives back, in its own currency.</summary>
    public static decimal Vat(SAPCreditNote creditNote) =>
        SapDocumentAmounts.VatSum(creditNote) ?? creditNote.VatSum;

    /// <summary>How much of the memo has been set against invoices or refunded, in its own currency.</summary>
    /// <remarks>
    /// A closed memo is used up whatever <c>PaidToDate</c> says — SAP closes one only when nothing is
    /// left on it. An open one reads <c>PaidToDate</c>, which SAP keeps in local currency.
    /// </remarks>
    public static decimal Applied(SAPCreditNote creditNote)
    {
        var total = Total(creditNote);
        if (!IsOpen(creditNote) || total <= 0m)
        {
            return total;
        }

        var applied = SapDocumentAmounts.ToDocumentCurrency(creditNote, creditNote.PaidToDate) ?? 0m;
        return Math.Clamp(Math.Round(applied, 2), 0m, total);
    }

    /// <summary>What is still to be used on the memo, in its own currency.</summary>
    public static decimal CreditLeft(SAPCreditNote creditNote) => Total(creditNote) - Applied(creditNote);

    /// <summary>A line's total before VAT, in the memo's currency.</summary>
    public static decimal LineNet(SAPCreditNote creditNote, SAPCreditNoteLine line) =>
        SapDocumentAmounts.LineTotal(creditNote, line) ?? line.LineTotal;

    /// <summary>A line's total with its VAT, in the memo's currency; zero when SAP sent no gross total.</summary>
    public static decimal LineGross(SAPCreditNote creditNote, SAPCreditNoteLine line)
    {
        if (!SapDocumentAmounts.IsForeignCurrency(creditNote))
        {
            return line.GrossTotal;
        }

        return line.GrossTotalFC is { } foreign && foreign != 0m
            ? foreign
            : SapDocumentAmounts.ToDocumentCurrency(creditNote, line.GrossTotal) ?? 0m;
    }

    /// <summary>The DocEntry of the A/R invoice the memo was raised against, when it was copied from one.</summary>
    public static int? CreditedInvoiceDocEntry(SAPCreditNote creditNote) =>
        creditNote.BaseEntry
        ?? creditNote.DocumentLines?
            .FirstOrDefault(line => line.BaseType == ArInvoiceObjectType && line.BaseEntry.HasValue)
            ?.BaseEntry;

    /// <summary>
    /// The reasons given on the memo's lines — SAP's own list (<c>U_Reasons</c>), never the header
    /// comments, which are staff notes and not written for the customer.
    /// </summary>
    public static string? Reason(SAPCreditNote creditNote)
    {
        var reasons = (creditNote.DocumentLines ?? [])
            .Select(line => line.CreditReason?.Trim())
            .Where(reason => !string.IsNullOrEmpty(reason))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }
}
