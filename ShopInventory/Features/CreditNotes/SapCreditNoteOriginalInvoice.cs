using System.Globalization;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes;

/// <summary>
/// The A/R invoice a SAP credit memo reverses, picked the way the fiscal platform picks it.
/// </summary>
/// <remarks>
/// <para>
/// The platform's rule (<c>SapCreditNoteLinkResolver</c> in the Fiscalisation repository), kept identical
/// here because the two of them file the same memos: when they disagree, one path refuses a memo the
/// other would file, or worse, files it against a different receipt — and a fiscal credit note filed
/// against the wrong original cannot be withdrawn.
/// </para>
/// <list type="number">
/// <item>Lines copied from exactly one A/R invoice (<c>BaseType</c> 13): that invoice. <c>NumAtCard</c> is
/// not read. Lines with no base link are manual additions to a copied memo and are ignored.</item>
/// <item>Lines copied from more than one invoice: refused. One fiscal credit note reverses one receipt.</item>
/// <item>Lines copied from something other than an invoice: refused.</item>
/// <item>Nothing copied: the invoice's DocNum typed into Customer Ref. No. (<c>NumAtCard</c>), used only
/// when SAP has exactly one live A/R invoice with that number for the memo's own customer. A typo or the
/// customer's own reference is refused rather than guessed into a link that would credit an unrelated
/// receipt.</item>
/// </list>
/// <para>
/// One copy of this rule for the app: the approval add starts reading the invoice before SAP converts the
/// draft, and it must start reading the same invoice the fiscaliser will use.
/// </para>
/// </remarks>
public static class SapCreditNoteOriginalInvoice
{
    /// <summary>SAP <c>ObjType</c> for an A/R invoice, the only base document a fiscal credit note may reverse.</summary>
    public const int ArInvoiceBaseType = 13;

    /// <summary>What the memo's lines were copied from. Reads nothing.</summary>
    public static SapCreditNoteBaseLink BaseLinkOf(SAPCreditNote creditNote)
    {
        var invoiceEntries = new SortedSet<int>();
        var otherBaseTypes = new SortedSet<int>();

        foreach (var line in creditNote.DocumentLines ?? [])
        {
            if (line.BaseEntry is not > 0)
            {
                continue;
            }

            if (line.BaseType == ArInvoiceBaseType)
            {
                invoiceEntries.Add(line.BaseEntry.Value);
            }
            else
            {
                // As the platform reads it: a base link with no type is not an invoice link.
                otherBaseTypes.Add(line.BaseType ?? 0);
            }
        }

        return new SapCreditNoteBaseLink([.. invoiceEntries], [.. otherBaseTypes]);
    }

    /// <summary>The invoice the memo reverses, read from SAP, or the sentence saying why there is none.</summary>
    /// <param name="sap">Reads the invoice.</param>
    /// <param name="creditNote">The memo, with its lines, customer and Customer Ref. No.</param>
    /// <param name="earlyRead">
    /// A read of the base invoice the caller started already. Used only when it is for the invoice the
    /// lines name and it answered.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<SapCreditNoteOriginalInvoiceResult> ResolveAsync(
        ISAPServiceLayerClient sap,
        SAPCreditNote creditNote,
        SapInvoiceRead? earlyRead,
        CancellationToken cancellationToken)
    {
        var link = BaseLinkOf(creditNote);

        if (link.SingleInvoiceDocEntry is int baseDocEntry)
        {
            var invoice = earlyRead is { } early && early.DocEntry == baseDocEntry
                ? await early.Read
                : null;
            invoice ??= await sap.GetInvoiceByDocEntryAsync(baseDocEntry, cancellationToken);

            return invoice is null
                ? SapCreditNoteOriginalInvoiceResult.Refused(
                    $"invoice DocEntry {baseDocEntry} could not be read from SAP, so the receipt it reverses cannot be found.")
                : SapCreditNoteOriginalInvoiceResult.Found(invoice);
        }

        if (link.InvoiceDocEntries.Count > 1)
        {
            return SapCreditNoteOriginalInvoiceResult.Refused(
                $"the credit note is copied from more than one invoice (base DocEntries {string.Join(", ", link.InvoiceDocEntries)}), " +
                "and a fiscal credit note reverses exactly one receipt. Split it into one credit note per invoice.");
        }

        if (link.OtherBaseTypes.Count > 0)
        {
            return SapCreditNoteOriginalInvoiceResult.Refused(
                $"the credit note is copied from base document type(s) {string.Join(", ", link.OtherBaseTypes)}, " +
                $"not an A/R invoice (type {ArInvoiceBaseType}), so there is no receipt for it to reverse.");
        }

        return await ResolveCustomerReferenceAsync(sap, creditNote, cancellationToken);
    }

    /// <summary>
    /// The DocNum in Customer Ref. No., accepted only when SAP has exactly one live A/R invoice with that
    /// DocNum for this memo's customer.
    /// </summary>
    private static async Task<SapCreditNoteOriginalInvoiceResult> ResolveCustomerReferenceAsync(
        ISAPServiceLayerClient sap,
        SAPCreditNote creditNote,
        CancellationToken cancellationToken)
    {
        var reference = creditNote.NumAtCard?.Trim();
        if (string.IsNullOrEmpty(reference)
            || !reference.All(char.IsAsciiDigit)
            || !int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var docNum)
            || docNum <= 0)
        {
            return SapCreditNoteOriginalInvoiceResult.Refused(
                string.IsNullOrEmpty(reference)
                    ? "the credit note is not based on an invoice and its Customer Ref. No. is empty, so there is no receipt for it to reverse. " +
                      "Put the original invoice's SAP number in Customer Ref. No., or create the credit note with Copy From on the invoice."
                    : $"the credit note is not based on an invoice and its Customer Ref. No. ('{reference}') is not an invoice number, " +
                      "so there is no receipt for it to reverse. Put the original invoice's SAP number in Customer Ref. No., " +
                      "or create the credit note with Copy From on the invoice.");
        }

        var cardCode = creditNote.CardCode?.Trim();
        var live = (await sap.GetInvoiceHeadersByDocNumAsync(docNum, cancellationToken))
            .Where(invoice => invoice.DocNum == docNum && !IsCancelled(invoice))
            .ToList();
        var own = live
            .Where(invoice => !string.IsNullOrEmpty(cardCode)
                              && string.Equals(invoice.CardCode?.Trim(), cardCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return own.Count switch
        {
            1 => SapCreditNoteOriginalInvoiceResult.Found(own[0]),
            0 when live.Count > 0 => SapCreditNoteOriginalInvoiceResult.Refused(
                $"the credit note names invoice {docNum} in Customer Ref. No., but that invoice belongs to " +
                $"{string.Join(", ", live.Select(invoice => invoice.CardCode).Distinct())}, not {cardCode}. " +
                "A credit note can only reverse its own customer's invoice."),
            0 => SapCreditNoteOriginalInvoiceResult.Refused(
                $"the credit note names invoice {docNum} in Customer Ref. No., but SAP has no live A/R invoice with that number. " +
                "Correct Customer Ref. No. to the original invoice's SAP number."),
            _ => SapCreditNoteOriginalInvoiceResult.Refused(
                $"the credit note names invoice {docNum} in Customer Ref. No., and SAP has {own.Count} A/R invoices with that number " +
                $"for {cardCode} (one per numbering series). Create the credit note with Copy From on the original invoice instead.")
        };
    }

    // The platform's test: Cancelled alone. The document that cancels an invoice is a credit memo, not an
    // invoice, so a cancellation never turns up in this read.
    private static bool IsCancelled(Invoice invoice) =>
        invoice.Cancelled?.Trim() is { } cancelled
        && (cancelled.Equals(SapYesNo.Yes, StringComparison.OrdinalIgnoreCase)
            || cancelled.Equals("Y", StringComparison.OrdinalIgnoreCase)
            || cancelled.Equals("true", StringComparison.OrdinalIgnoreCase));
}
