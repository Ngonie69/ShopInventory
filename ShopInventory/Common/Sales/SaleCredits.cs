using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
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

    /// <summary>
    /// The invoice lines the credits gave back, one entry per credited line of each credit. Filled by
    /// <see cref="SaleCredits.ForInvoicesAsync"/>; read through <see cref="SaleCredits.ByInvoiceLine"/>.
    /// </summary>
    public IReadOnlyList<CreditedLine> Lines { get; init; } = [];

    /// <summary>Whether this credit gives back the whole of <paramref name="total"/>, to the cent.</summary>
    public bool Covers(decimal total) => Amount > 0m && Amount >= Math.Abs(total) - 0.01m;
}

/// <summary>One line of one credit, as far as it can be tied to the invoice it reverses.</summary>
/// <param name="InvoiceLine">
/// The invoice's <c>LineNum</c> the credit names — a memo line's <c>BaseLine</c>, or a till credit's line
/// on a sale that posted one-to-one. Null for a till credit on a consolidated sale, whose invoice line is
/// shared with the rest of the day and is found by <paramref name="ItemCode"/> instead.
/// </param>
/// <param name="Quantity">
/// How many were given back. A till credit knows; the SAP memo projection stores no quantity, so a memo
/// line leaves it null and <see cref="SaleCredits.ByInvoiceLine"/> works it out from <paramref name="Net"/>
/// and the invoice line's own price.
/// </param>
/// <param name="Amount">What was given back, tax included.</param>
/// <param name="Net">What was given back before tax, where the credit records it.</param>
public sealed record CreditedLine(int? InvoiceLine, string? ItemCode, decimal? Quantity, decimal Amount, decimal? Net);

/// <summary>What has been credited against one invoice line, over all of its credits.</summary>
public sealed record InvoiceLineCredit(decimal Quantity, decimal Amount);

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
    /// <summary>
    /// The credits against each SAP invoice, by invoice DocEntry, with the lines they gave back. Invoices
    /// with none are absent.
    /// </summary>
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
                credit.PlanJson,
                SaleInvoice = credit.Sale.SapDocEntry,
                ConsolidatedInvoice = credit.Sale.Consolidation != null ? credit.Sale.Consolidation.SapDocEntry : null
            })
            .ToListAsync(ct);

        var tillNumbers = tillCredits.Count == 0
            ? []
            : await DesktopCreditNoteNumber.ForSalesAsync(db, tillCredits.Select(c => c.SaleId), ct);

        var saleIds = tillCredits.Select(credit => credit.SaleId).Distinct().ToList();
        var saleLines = saleIds.Count == 0
            ? Array.Empty<DesktopSaleLineEntity>().ToLookup(line => line.SaleId)
            : (await db.DesktopSaleLines
                    .AsNoTracking()
                    .Where(line => saleIds.Contains(line.SaleId))
                    .Select(line => new DesktopSaleLineEntity
                    {
                        Id = line.Id,
                        SaleId = line.SaleId,
                        LineNum = line.LineNum,
                        ItemCode = line.ItemCode
                    })
                    .ToListAsync(ct))
                .ToLookup(line => line.SaleId);

        foreach (var credit in tillCredits)
        {
            var invoice = credit.SaleInvoice ?? credit.ConsolidatedInvoice;
            if (invoice is not { } docEntry || !entries.Contains(docEntry))
                continue;

            if (credit.SapDocEntry is { } memo && memosByInvoice[docEntry].Contains(memo))
                continue;

            parts.Add(new CreditPart(
                docEntry,
                null,
                credit.Amount,
                tillNumbers.GetValueOrDefault(credit.Id, ""),
                TillCreditLines(credit.PlanJson, saleLines[credit.SaleId].ToList(), perSaleInvoice: credit.SaleInvoice is not null)));
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

            parts.Add(new CreditPart(credit.SaleId, null, credit.Amount, tillNumbers.GetValueOrDefault(credit.Id, ""), []));
        }

        return Summarise(parts);
    }

    /// <summary>
    /// What has been credited against each line of an invoice, by the invoice line's <c>LineNum</c>.
    /// Lines nothing was credited against are absent.
    /// </summary>
    /// <param name="credit">The invoice's credits, as <see cref="ForInvoicesAsync"/> answers them.</param>
    /// <param name="invoiceLines">The invoice's own lines, with their net line totals.</param>
    /// <remarks>
    /// A credited line that names its invoice line lands on it; one that does not — a till credit on a
    /// consolidated sale — lands on the first invoice line carrying its item. A memo line's quantity is
    /// worked out from its net and the invoice line's net price per unit, because the memo projection
    /// stores none; to three places, which is as fine as SAP sells anything.
    /// </remarks>
    public static Dictionary<int, InvoiceLineCredit> ByInvoiceLine(
        SaleCredit? credit,
        IReadOnlyCollection<(int LineNum, string? ItemCode, decimal Quantity, decimal LineTotal)> invoiceLines)
    {
        var result = new Dictionary<int, InvoiceLineCredit>();
        if (credit is null || credit.Lines.Count == 0 || invoiceLines.Count == 0)
            return result;

        var lines = invoiceLines.ToList();

        foreach (var credited in credit.Lines)
        {
            var index = credited.InvoiceLine is { } number ? lines.FindIndex(line => line.LineNum == number) : -1;

            if (index < 0 && !string.IsNullOrWhiteSpace(credited.ItemCode))
            {
                index = lines.FindIndex(line =>
                    string.Equals(line.ItemCode, credited.ItemCode, StringComparison.OrdinalIgnoreCase));
            }

            if (index < 0)
                continue;

            var target = lines[index];
            var quantity = credited.Quantity
                ?? (credited.Net is { } net && target.Quantity != 0m && target.LineTotal != 0m
                    ? Math.Round(net / (target.LineTotal / target.Quantity), 3, MidpointRounding.AwayFromZero)
                    : 0m);

            result[target.LineNum] = result.TryGetValue(target.LineNum, out var soFar)
                ? new InvoiceLineCredit(soFar.Quantity + quantity, soFar.Amount + credited.Amount)
                : new InvoiceLineCredit(quantity, credited.Amount);
        }

        return result;
    }

    /// <summary>
    /// Puts an invoice's credits on it for the till: the credited total and numbers on the header, and on
    /// each line what was given back of it. A line nothing was credited against reads 0, not null, so the
    /// till can tell "looked, and none" from a read that did not look.
    /// </summary>
    public static void ApplyTo(DTOs.InvoiceDto invoice, SaleCredit? credit)
    {
        credit ??= SaleCredit.None;
        invoice.CreditedAmount = credit.Amount;
        invoice.CreditNoteNumbers = credit.Numbers.ToList();

        if (invoice.Lines is not { Count: > 0 } lines)
            return;

        var byLine = ByInvoiceLine(
            credit, lines.Select(line => (line.LineNum, line.ItemCode, line.Quantity, line.LineTotal)).ToList());

        foreach (var line in lines)
        {
            var credited = byLine.GetValueOrDefault(line.LineNum);
            line.CreditedQuantity = credited?.Quantity ?? 0m;
            line.CreditedAmount = credited?.Amount ?? 0m;
        }
    }

    /// <summary>
    /// The lines of a till credit, from the plan it was filed with. An unreadable plan gives none: the
    /// credit's amount still counts, only its lines cannot be shown.
    /// </summary>
    private static List<CreditedLine> TillCreditLines(
        string planJson, List<DesktopSaleLineEntity> saleLines, bool perSaleInvoice)
    {
        DesktopCreditPlan? plan;

        try
        {
            plan = JsonSerializer.Deserialize<DesktopCreditPlan>(planJson, DesktopCreditNoteService.Json);
        }
        catch (JsonException)
        {
            return [];
        }

        if (plan is null)
            return [];

        // A receipt line is found by position on the receipt, and an invoice line by position on the
        // invoice — never by LineNum, which a sale can repeat. See DesktopSaleLineOrder.
        var invoiceOrder = DesktopSaleLineOrder.Invoice(saleLines);

        return plan.Quantities
            .Where(quantity => quantity.Quantity > 0)
            .Select(quantity =>
            {
                var saleLine = DesktopSaleLineOrder.ForReceiptLine(saleLines, quantity.LineNo);
                var source = plan.Source.Lines.FirstOrDefault(line => line.LineNo == quantity.LineNo);
                var index = saleLine is null ? -1 : invoiceOrder.IndexOf(saleLine);

                return new CreditedLine(
                    perSaleInvoice && index >= 0 ? index : null,
                    saleLine?.ItemCode,
                    quantity.Quantity,
                    Math.Round(quantity.Quantity * (source?.UnitPrice ?? 0m), 2, MidpointRounding.AwayFromZero),
                    null);
            })
            .ToList();
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
                line.BaseLine,
                line.ItemCode,
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
                memo.First().SapDocNum.ToString(CultureInfo.InvariantCulture),
                memo.Select(line => new CreditedLine(
                        line.BaseLine,
                        line.ItemCode,
                        null,
                        Math.Abs(line.LineTotal + line.VatSum),
                        Math.Abs(line.LineTotal)))
                    .ToList()))
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
                        .ToList())
                {
                    Lines = document.SelectMany(part => part.Lines).ToList()
                });

    /// <param name="Invoice">The document the credit is against: an invoice DocEntry or a sale id.</param>
    private sealed record CreditPart(
        int Invoice, int? MemoDocEntry, decimal Amount, string Number, IReadOnlyList<CreditedLine> Lines);
}
