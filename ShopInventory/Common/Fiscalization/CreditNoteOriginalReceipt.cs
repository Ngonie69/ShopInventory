using System.Globalization;
using ShopInventory.Configuration;
using ShopInventory.Data;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// The number the fiscal device holds a credit note's original receipt under.
/// </summary>
/// <remarks>
/// A credit note has to reference the receipt it reverses, and the device answers only for the number
/// that receipt was filed under. That is not one thing:
///
/// <list type="bullet">
/// <item>An ordinary SAP invoice is filed under its <b>DocNum</b>. Callers used to pass the DocEntry,
/// which is a different number altogether — 2342939 against DocNum 772109 — so the lookup found
/// nothing, or found some other document that happened to carry that number.</item>
/// <item>A till, van or vending sale is fiscalised <b>before</b> it reaches SAP, under the sale's own
/// external reference (<see cref="FiscalisationSettings.BuildPreSapInvoiceNo"/>). The device holds
/// nothing under the SAP invoice's number at all: till invoice 772109 is receipt 216877 under
/// <c>GRC-FAC-20260911-286EEC7389FD</c>.</item>
/// <item>An end-of-day consolidated invoice stands for many sales, each with its own receipt. No single
/// receipt is the original, so there is nothing a credit note against it could reference.</item>
/// <item>An invoice reposted after the SAP update (<see cref="RepostedInvoiceMarker"/>) was fiscalised
/// under its <b>old</b> number, and the device holds nothing under the new one. It is refused rather
/// than redirected to the old number: that number was a DocNum in the company as it stood before the
/// update, and which receipt it was filed under — an ordinary invoice, a till sale, a consolidation —
/// is exactly what the registries above answer by DocNum, for documents that may since share it. A
/// wrong original on a fiscal credit note cannot be withdrawn, so a person files it.</item>
/// </list>
///
/// The two registries are the same markers the invoice guards read, for the same reason: they are
/// written in the SaveChanges that records the SAP post, so they cannot be missing while the invoice
/// exists.
/// </remarks>
internal static class CreditNoteOriginalReceipt
{
    /// <param name="dbContext">Holds the consolidation and per-sale registries.</param>
    /// <param name="originalInvoiceDocNum">The DocNum of the invoice the credit note is based on.</param>
    /// <param name="originalInvoiceComments">
    /// That invoice's SAP remarks, which say whether it was reposted after the SAP update. Required, so
    /// that no caller can resolve a receipt without having read the invoice.
    /// </param>
    /// <param name="settings">The pre-SAP prefix and the reposted-invoice marker.</param>
    /// <param name="cancellationToken">Cancels the registry reads.</param>
    public static async Task<CreditNoteOriginalReceiptNumber> ResolveAsync(
        ApplicationDbContext dbContext,
        int originalInvoiceDocNum,
        string? originalInvoiceComments,
        FiscalisationSettings settings,
        CancellationToken cancellationToken)
    {
        if (originalInvoiceDocNum <= 0)
        {
            return CreditNoteOriginalReceiptNumber.Refused(
                "the original invoice's SAP document number is not known, so the receipt it reverses cannot be found.");
        }

        if (RepostedInvoiceMarker.IsReposted(settings, originalInvoiceComments))
        {
            var oldNumber = RepostedInvoiceMarker.OldInvoiceNumber(originalInvoiceComments);

            return CreditNoteOriginalReceiptNumber.Refused(
                $"invoice {originalInvoiceDocNum} was reposted after the SAP update and is fiscalised under its old "
                + $"number{(oldNumber is null ? string.Empty : $" ({oldNumber})")}, not this one. The device holds no "
                + "receipt under this number, so this credit note has to be fiscalised by hand against the original "
                + "receipt.");
        }

        var consolidation = await ConsolidatedInvoiceRegistry.FindByDocNumAsync(
            dbContext, originalInvoiceDocNum, cancellationToken);

        if (consolidation is not null)
        {
            return CreditNoteOriginalReceiptNumber.Refused(
                $"invoice {originalInvoiceDocNum} consolidates {consolidation.SaleCount} sale(s), each fiscalised "
                + "under its own receipt before it reached SAP. A credit note references one receipt, so it has to "
                + "be fiscalised against the sale being reversed.");
        }

        var sale = await PerSaleInvoiceRegistry.FindByDocNumAsync(
            dbContext, originalInvoiceDocNum, cancellationToken);

        if (sale is not null && !string.IsNullOrWhiteSpace(sale.ExternalReferenceId))
        {
            return CreditNoteOriginalReceiptNumber.Found(settings.BuildPreSapInvoiceNo(sale.ExternalReferenceId));
        }

        return CreditNoteOriginalReceiptNumber.Found(
            originalInvoiceDocNum.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Either the invoice number to ask the device for, or why there is none.
/// </summary>
internal sealed record CreditNoteOriginalReceiptNumber(string? InvoiceNumber, string? Refusal)
{
    public static CreditNoteOriginalReceiptNumber Found(string invoiceNumber) => new(invoiceNumber, null);

    public static CreditNoteOriginalReceiptNumber Refused(string reason) => new(null, reason);
}
