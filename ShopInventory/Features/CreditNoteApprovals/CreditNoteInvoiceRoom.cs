using System.Globalization;
using ErrorOr;
using ShopInventory.Common.Errors;
using ShopInventory.Features.CreditNotes;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>
/// What is left to credit on the invoice a held credit memo draft reverses, and the guard that refuses
/// to add a draft that credits more than that.
/// </summary>
/// <remarks>
/// <para>
/// ZIMRA refuses a credit note that, with the credit notes already filed against its receipt, credits more
/// than the receipt (RCPT035). SAP does not stop it: it caps only the lines copied from the invoice at
/// their open quantity, so a line typed onto a copied memo — wash bay crates, mostly — takes it past the
/// invoice. Added anyway, the memo is in SAP and the customer's balance, and can never be fiscalised —
/// one added through this page in November 2026 credited USD 1,386.00 against an invoice of USD 1,155.00.
/// </para>
/// <para>
/// The invoice is the one the fiscal platform files the memo against
/// (<see cref="SapCreditNoteOriginalInvoice"/>), and what it has already been credited is every live
/// credit note of the customer filed against it by the same rule: copied from it, or with nothing copied
/// and its number in Customer Ref. No. — the same count as the HANA guard <c>KEF_CREDIT_MEMO_CHECK</c>
/// (scripts/sap-credit-memo-guard). Totals are compared in SAP's local currency (USD), which is what
/// <c>DocTotal</c> carries on every document.
/// </para>
/// <para>
/// A draft the platform could not file at all — copied from several invoices, or naming none it can find —
/// is not this guard's to refuse; the fiscaliser says why after the add. Anything this guard cannot read
/// refuses the write: guessing wrong strands a memo that cannot be fiscalised.
/// </para>
/// </remarks>
internal static class CreditNoteInvoiceRoom
{
    /// <summary>Totals are money to the cent; a cent of rounding between SAP's documents is not an over-credit.</summary>
    private const decimal Tolerance = 0.01m;

    /// <summary>What has been credited against an invoice already, and so what is left.</summary>
    /// <param name="Invoice">The invoice the draft reverses.</param>
    /// <param name="EarlierCreditNotes">The live credit notes already filed against it.</param>
    internal sealed record Room(Invoice Invoice, IReadOnlyList<SAPCreditNote> EarlierCreditNotes)
    {
        public decimal InvoiceTotal => Math.Abs(Invoice.DocTotal);

        public decimal AlreadyCredited => EarlierCreditNotes.Sum(note => Math.Abs(note.DocTotal));

        public decimal Left => Math.Max(0m, InvoiceTotal - AlreadyCredited);

        public bool Admits(SAPCreditNote draft) => Math.Abs(draft.DocTotal) <= Left + Tolerance;
    }

    /// <summary>
    /// The room on the invoice the draft reverses: null when the draft names no invoice the platform could
    /// file it against. Throws when SAP cannot be read.
    /// </summary>
    public static async Task<Room?> ReadAsync(
        ISAPServiceLayerClient sap,
        SAPCreditNote draft,
        CancellationToken cancellationToken)
    {
        var link = SapCreditNoteOriginalInvoice.BaseLinkOf(draft);
        var resolved = await SapCreditNoteOriginalInvoice.ResolveAsync(sap, draft, earlyRead: null, cancellationToken);

        if (resolved.Invoice is not Invoice invoice)
        {
            // Copied from one invoice that SAP could not hand back is a failed read, not a draft with no invoice.
            if (link.SingleInvoiceDocEntry is int unread)
            {
                throw new InvalidOperationException($"Invoice DocEntry {unread} could not be read from SAP.");
            }

            return null;
        }

        var cardCode = draft.CardCode?.Trim();
        if (string.IsNullOrEmpty(cardCode))
        {
            return null;
        }

        // A memo cannot be dated before its invoice in SAP, so the invoice's date bounds the read; the upper
        // bound only has to lie past any memo already entered.
        var from = CreditNoteApprovalProjection.ParseSapDate(invoice.DocDate)?.Date ?? new DateTime(2000, 1, 1);
        var to = DateTime.UtcNow.Date.AddYears(1);
        var customerCreditNotes = await sap.GetCreditNotesByCustomerAsync(cardCode, from, to, cancellationToken);

        return new Room(invoice, EarlierCreditsAgainst(invoice, customerCreditNotes));
    }

    /// <summary>The live credit notes among <paramref name="creditNotes"/> the platform files against <paramref name="invoice"/>.</summary>
    public static IReadOnlyList<SAPCreditNote> EarlierCreditsAgainst(
        Invoice invoice,
        IEnumerable<SAPCreditNote> creditNotes)
    {
        var invoiceNumber = invoice.DocNum.ToString(CultureInfo.InvariantCulture);

        return creditNotes
            .Where(note => Credits(note))
            .Where(note =>
            {
                var link = SapCreditNoteOriginalInvoice.BaseLinkOf(note);
                if (link.InvoiceDocEntries.Count > 0 || link.OtherBaseTypes.Count > 0)
                {
                    return link.SingleInvoiceDocEntry == invoice.DocEntry;
                }

                return string.Equals(note.NumAtCard?.Trim(), invoiceNumber, StringComparison.Ordinal);
            })
            .DistinctBy(note => note.DocEntry)
            .OrderBy(note => note.DocEntry)
            .ToList();
    }

    /// <summary>
    /// The refusal for a draft whose total does not fit on <paramref name="room"/>; success when it does,
    /// or when the draft names no invoice.
    /// </summary>
    public static ErrorOr<Success> Check(SAPCreditNote draft, Room? room, ILogger logger)
    {
        if (room is null || room.Admits(draft))
        {
            return Result.Success;
        }

        logger.LogWarning(
            "Draft {DraftEntry} credits {DraftTotal} against invoice {InvoiceDocNum} of {InvoiceTotal} with {Credited} already credited; refusing the add",
            draft.DocEntry, Math.Abs(draft.DocTotal), room.Invoice.DocNum, room.InvoiceTotal, room.AlreadyCredited);
        return Errors.CreditNoteApproval.ExceedsInvoice(Describe(draft, room));
    }

    /// <summary>The sentence the page shows and the refusal carries.</summary>
    public static string Describe(SAPCreditNote draft, Room room)
    {
        var earlier = room.EarlierCreditNotes.Count switch
        {
            0 => string.Empty,
            1 => $", and credit note {room.EarlierCreditNotes[0].DocNum} has already credited {Money(room.AlreadyCredited)} of it",
            _ => $", and credit notes {string.Join(", ", room.EarlierCreditNotes.Select(note => note.DocNum))} have already credited {Money(room.AlreadyCredited)} of it"
        };

        var left = room.Left > 0
            ? $"Only {Money(room.Left)} is left to credit."
            : "Nothing is left to credit on it.";

        return $"Not added: this credit note is {Money(Math.Abs(draft.DocTotal))}, but invoice {room.Invoice.DocNum} is "
               + $"{Money(room.InvoiceTotal)}{earlier}. {left} ZIMRA will not fiscalise a credit note that credits more "
               + "than its invoice. Lower the quantities, or correct the draft in SAP, so it is no more than what is left.";
    }

    private static string Money(decimal amount) => "USD " + amount.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>Neither a cancelled credit note nor the document that cancelled one credits anybody.</summary>
    private static bool Credits(SAPCreditNote creditNote) =>
        !string.Equals(creditNote.Cancelled, SapYesNo.Yes, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(creditNote.CancelStatus)
            || string.Equals(creditNote.CancelStatus, "csNo", StringComparison.OrdinalIgnoreCase));
}
