using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// How much of a document has been given back, and under which credit numbers.
/// </summary>
/// <param name="Amount">
/// The tax-inclusive total credited — the same basis as an invoice's <c>DocTotal</c> and a sale's
/// <c>TotalAmount</c>, so a client nets one against the other without knowing about VAT.
/// </param>
/// <param name="Numbers">The credit numbers a person would quote: SAP memo DocNums and <c>CN1753</c>-style till credits.</param>
public sealed record SaleCredit(decimal Amount, IReadOnlyList<string> Numbers)
{
    public static readonly SaleCredit None = new(0m, []);

    /// <summary>Whether this credit gives back the whole of <paramref name="total"/>, to the cent.</summary>
    public bool Covers(decimal total) => Amount > 0m && Amount >= Math.Abs(total) - 0.01m;
}

/// <summary>
/// The credits against a page of invoices or sales, for the van handset's history and the till's lists.
/// </summary>
/// <remarks>
/// <para>
/// A credit is recorded in one of two places, and both have to be read. A SAP credit memo — raised in SAP,
/// by invoice cancellation, or by a till credit once SAP has taken it — is in the memo projection, tied to
/// its invoice by a line whose base document is that invoice. A till credit ZIMRA has accepted but SAP has
/// not (deferred, failed, or a consolidated sale's credit that is keyed in SAP by hand) is only in
/// <c>DesktopCreditNotes</c>. Once SAP takes a till credit it is in both, so a till credit whose memo is
/// already counted against the same invoice is skipped.
/// </para>
/// <para>
/// Only credits that actually give something back count: a cancelled memo does not, and a till credit
/// counts once it is <see cref="DesktopCreditStatuses.Fiscalised"/> — the rule the console's drawers use.
/// </para>
/// </remarks>
public static class SaleCredits
{
    /// <summary>The credits against each SAP invoice, by invoice DocEntry. Invoices with none are absent.</summary>
    public static async Task<Dictionary<int, SaleCredit>> ForInvoicesAsync(
        ApplicationDbContext db,
        IEnumerable<int> invoiceDocEntries,
        CancellationToken ct)
    {
        var entries = invoiceDocEntries.Distinct().ToList();
        if (entries.Count == 0)
            return [];

        var parts = await ReadMemosAsync(db, entries, ct);
        var memosByInvoice = parts.ToLookup(part => part.Invoice, part => part.MemoDocEntry);

        // A per-sale invoice is the sale's own; a consolidated one is its consolidation's. A credit on
        // any sale in a consolidation is a credit against that consolidated invoice.
        var tillCredits = await db.DesktopCreditNotes
            .AsNoTracking()
            .Where(credit => credit.Status == DesktopCreditStatuses.Fiscalised
                             && ((credit.Sale.SapDocEntry != null && entries.Contains(credit.Sale.SapDocEntry.Value))
                                 || (credit.Sale.Consolidation != null
                                     && credit.Sale.Consolidation.SapDocEntry != null
                                     && entries.Contains(credit.Sale.Consolidation.SapDocEntry.Value))))
            .Select(credit => new
            {
                credit.Id,
                credit.SaleId,
                credit.Amount,
                credit.SapDocEntry,
                SaleInvoice = credit.Sale.SapDocEntry,
                ConsolidatedInvoice = credit.Sale.Consolidation != null ? credit.Sale.Consolidation.SapDocEntry : null
            })
            .ToListAsync(ct);

        var tillNumbers = tillCredits.Count == 0
            ? []
            : await DesktopCreditNoteNumber.ForSalesAsync(db, tillCredits.Select(c => c.SaleId), ct);

        foreach (var credit in tillCredits)
        {
            var invoice = credit.SaleInvoice ?? credit.ConsolidatedInvoice;
            if (invoice is not { } docEntry || !entries.Contains(docEntry))
                continue;

            if (credit.SapDocEntry is { } memo && memosByInvoice[docEntry].Contains(memo))
                continue;

            parts.Add(new CreditPart(docEntry, null, credit.Amount, tillNumbers.GetValueOrDefault(credit.Id, "")));
        }

        return Summarise(parts);
    }

    /// <summary>The credits against each till or van sale, by sale id. Sales with none are absent.</summary>
    /// <remarks>
    /// A sale's own till credits, plus any SAP memo against the sale's own invoice when it posted
    /// one-to-one — which is how an invoice cancelled in SAP reaches the sale. A consolidated sale's invoice
    /// is shared with the rest of its day, so a memo against it cannot be put on any one sale and is not.
    /// </remarks>
    public static async Task<Dictionary<int, SaleCredit>> ForSalesAsync(
        ApplicationDbContext db,
        IEnumerable<(int SaleId, int? SapDocEntry)> sales,
        CancellationToken ct)
    {
        var page = sales.DistinctBy(sale => sale.SaleId).ToList();
        if (page.Count == 0)
            return [];

        var saleIds = page.Select(sale => sale.SaleId).ToList();

        var tillCredits = await db.DesktopCreditNotes
            .AsNoTracking()
            .Where(credit => saleIds.Contains(credit.SaleId) && credit.Status == DesktopCreditStatuses.Fiscalised)
            .Select(credit => new { credit.Id, credit.SaleId, credit.Amount, credit.SapDocEntry })
            .ToListAsync(ct);

        var tillNumbers = tillCredits.Count == 0
            ? []
            : await DesktopCreditNoteNumber.ForSalesAsync(db, tillCredits.Select(c => c.SaleId), ct);

        var saleByInvoice = page
            .Where(sale => sale.SapDocEntry is not null)
            .GroupBy(sale => sale.SapDocEntry!.Value)
            .Where(invoice => invoice.Count() == 1)
            .ToDictionary(invoice => invoice.Key, invoice => invoice.Single().SaleId);

        var parts = new List<CreditPart>();
        var memosBySale = new Dictionary<int, HashSet<int>>();

        foreach (var memo in await ReadMemosAsync(db, saleByInvoice.Keys.ToList(), ct))
        {
            var saleId = saleByInvoice[memo.Invoice];
            parts.Add(memo with { Invoice = saleId });

            if (!memosBySale.TryGetValue(saleId, out var counted))
                memosBySale[saleId] = counted = [];
            counted.Add(memo.MemoDocEntry!.Value);
        }

        foreach (var credit in tillCredits)
        {
            if (credit.SapDocEntry is { } memo
                && memosBySale.TryGetValue(credit.SaleId, out var counted)
                && counted.Contains(memo))
                continue;

            parts.Add(new CreditPart(credit.SaleId, null, credit.Amount, tillNumbers.GetValueOrDefault(credit.Id, "")));
        }

        return Summarise(parts);
    }

    /// <summary>Each live SAP memo against the given invoices, one part per memo and invoice.</summary>
    /// <remarks>
    /// Summed from the lines based on that invoice rather than taken from the memo's total, because one
    /// memo can credit lines from several invoices.
    /// </remarks>
    private static async Task<List<CreditPart>> ReadMemosAsync(
        ApplicationDbContext db,
        List<int> invoiceDocEntries,
        CancellationToken ct)
    {
        if (invoiceDocEntries.Count == 0)
            return [];

        var lines = await db.SapCreditNoteLineSnapshots
            .AsNoTracking()
            .Where(line => line.BaseType == VanSaleCreditNotes.InvoiceBaseType
                           && line.BaseEntry != null
                           && invoiceDocEntries.Contains(line.BaseEntry.Value)
                           && !line.CreditNote.IsCancelled)
            .Select(line => new
            {
                Invoice = line.BaseEntry!.Value,
                line.CreditNoteDocEntry,
                line.CreditNote.SapDocNum,
                line.LineTotal,
                line.VatSum
            })
            .ToListAsync(ct);

        return lines
            .GroupBy(line => (line.Invoice, line.CreditNoteDocEntry))
            .Select(memo => new CreditPart(
                memo.Key.Invoice,
                memo.Key.CreditNoteDocEntry,
                memo.Sum(line => Math.Abs(line.LineTotal + line.VatSum)),
                memo.First().SapDocNum.ToString(CultureInfo.InvariantCulture)))
            .ToList();
    }

    private static Dictionary<int, SaleCredit> Summarise(IEnumerable<CreditPart> parts) =>
        parts
            .GroupBy(part => part.Invoice)
            .ToDictionary(
                document => document.Key,
                document => new SaleCredit(
                    document.Sum(part => part.Amount),
                    document
                        .Select(part => part.Number)
                        .Where(number => !string.IsNullOrWhiteSpace(number))
                        .Distinct(StringComparer.Ordinal)
                        .ToList()));

    /// <param name="Invoice">The document the credit is against: an invoice DocEntry or a sale id.</param>
    private sealed record CreditPart(int Invoice, int? MemoDocEntry, decimal Amount, string Number);
}
