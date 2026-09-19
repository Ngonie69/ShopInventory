using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.DesktopCreditNotes;

/// <summary>
/// Names the batches a desktop credit memo returns, taken from the invoice it credits.
/// </summary>
/// <remarks>
/// <para>
/// SAP refuses a batch-managed credit memo line that names no batch — "Cannot add row without complete
/// selection of batch/serial numbers" — and refuses the whole memo with it. Basing the line on the
/// invoice (BaseType/BaseEntry/BaseLine) does <b>not</b> make SAP take the invoice's batches; that was
/// assumed when the poster was written and it is why INV1753's credit (19 September 2026) was
/// fiscalised and then refused by SAP.
/// </para>
/// <para>
/// A till sale's lines carry no batch, so the invoice is the only record of which batches left. Each
/// credited quantity is taken from the invoice line's batches in the order SAP lists them, after
/// replaying the credits already posted against the same sale the same way. That replay is what keeps a
/// second partial credit from returning into a batch the first one already filled back up.
/// </para>
/// </remarks>
public static class CreditMemoBatchSelection
{
    /// <summary>
    /// Sets <c>BatchNumbers</c> on every line of <paramref name="request"/> whose invoice line carried
    /// batches. Lines of items that are not batch-managed are left as they are.
    /// </summary>
    /// <param name="request">The memo, already based on <paramref name="invoice"/> line by line.</param>
    /// <param name="invoice">The invoice as SAP holds it, batches included.</param>
    /// <param name="alreadyCredited">
    /// Quantities of earlier credits already in SAP, keyed by invoice line (BaseLine), oldest first.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The invoice does not match the memo, or its batches cannot cover the quantity. Either way nothing
    /// SAP would accept can be built, and guessing would return stock into the wrong batch.
    /// </exception>
    public static void Apply(
        CreateCreditNoteRequest request,
        Invoice invoice,
        IReadOnlyList<(int InvoiceLine, decimal Quantity)> alreadyCredited)
    {
        var invoiceLines = invoice.DocumentLines ?? [];

        foreach (var line in request.Lines)
        {
            var baseLine = line.OriginalInvoiceLineId
                ?? throw new InvalidOperationException(
                    $"The credit line for {line.ItemCode} names no invoice line.");

            var invoiceLine = invoiceLines.SingleOrDefault(l => l.LineNum == baseLine)
                ?? throw new InvalidOperationException(
                    $"SAP invoice {invoice.DocNum} has no line {baseLine} for {line.ItemCode}.");

            if (!string.Equals(invoiceLine.ItemCode, line.ItemCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SAP invoice {invoice.DocNum} line {baseLine} is {invoiceLine.ItemCode}, not "
                    + $"{line.ItemCode}. The credit memo would credit the wrong item.");
            }

            var batches = (invoiceLine.BatchNumbers ?? [])
                .Where(b => !string.IsNullOrWhiteSpace(b.BatchNumber) && b.Quantity > 0)
                .ToList();

            if (batches.Count == 0)
            {
                // Not batch-managed. SAP needs nothing here.
                line.BatchNumbers = null;
                continue;
            }

            var remaining = batches.Select(b => b.Quantity).ToArray();

            foreach (var earlier in alreadyCredited.Where(c => c.InvoiceLine == baseLine))
            {
                // A shortfall here means a credit was posted that this replay cannot place — keyed in SAP
                // by hand, or against batches that have since changed. What is left is then unknown.
                if (Take(remaining, earlier.Quantity) is null)
                {
                    throw new InvalidOperationException(
                        $"Earlier credits against invoice {invoice.DocNum} line {baseLine} ({line.ItemCode}) "
                        + "return more than its batches issued, so the batch for this return is unknown.");
                }
            }

            var taken = Take(remaining, line.Quantity)
                ?? throw new InvalidOperationException(
                    $"Invoice {invoice.DocNum} line {baseLine} issued {batches.Sum(b => b.Quantity)} of "
                    + $"{line.ItemCode} in batches, and too little of that is left uncredited to return "
                    + $"{line.Quantity}.");

            line.BatchNumbers = taken
                .Select((quantity, index) => (quantity, index))
                .Where(t => t.quantity > 0)
                .Select(t => new CreditNoteBatchRequest
                {
                    BatchNumber = batches[t.index].BatchNumber,
                    Quantity = t.quantity
                })
                .ToList();
        }
    }

    /// <summary>
    /// Takes <paramref name="quantity"/> from <paramref name="remaining"/> in order, and answers how
    /// much came from each — or null, leaving <paramref name="remaining"/> untouched, if it cannot.
    /// </summary>
    private static decimal[]? Take(decimal[] remaining, decimal quantity)
    {
        if (remaining.Sum() < quantity)
        {
            return null;
        }

        var taken = new decimal[remaining.Length];
        var owed = quantity;

        for (var i = 0; i < remaining.Length && owed > 0; i++)
        {
            var take = Math.Min(remaining[i], owed);
            taken[i] = take;
            remaining[i] -= take;
            owed -= take;
        }

        return taken;
    }
}
