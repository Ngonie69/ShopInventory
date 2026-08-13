using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// Answers whether a SAP invoice is the record of a sale that was already fiscalised, one receipt at
/// a time, before it reached SAP.
/// </summary>
/// <remarks>
/// The sibling of <see cref="ConsolidatedInvoiceRegistry"/>, for the routes that post one invoice per
/// sale instead of one per customer per day: van sales, shop till sales and vending.
///
/// Without it those invoices are indistinguishable from an ordinary unfiscalised SAP invoice. Nothing
/// downstream can see the receipt, because the receipt was signed under the sale's own external
/// reference while every lookup here is keyed on the SAP DocNum — two different numbers, so even
/// FDMS's own duplicate guard, keyed on (TaxPayerTIN, ReceiptType, InvoiceNo), cannot catch it. The
/// invoice therefore reads "Unknown", the backfill records it as "Not Fiscalised", the Fiscalise
/// button appears, and one click submits a sale the customer already holds a receipt for to FDMS a
/// second time. That is irreversible: a duplicate fiscal receipt cannot be withdrawn.
///
/// The marker is the sale row itself. <c>SapDocNum</c> is written in the same SaveChanges that records
/// the post, so it cannot go missing while the invoice exists — the same reason the consolidated
/// registry reads its own table rather than the fiscal transaction log, whose write is best-effort and
/// happens after the SAP post has already committed.
/// </remarks>
internal static class PerSaleInvoiceRegistry
{
    /// <summary>
    /// Returns the fiscalised sale that produced <paramref name="docNum"/>, or null if that invoice
    /// did not come from one.
    /// </summary>
    /// <remarks>
    /// Filtered on a successful fiscalisation, unlike the consolidated registry. There the receipts
    /// are with FDMS regardless of what the consolidation did afterwards; here the sale IS the
    /// receipt, so a sale that never fiscalised has nothing to protect and its invoice should stay
    /// fiscalisable by hand.
    /// </remarks>
    public static async Task<DesktopSaleEntity?> FindByDocNumAsync(
        ApplicationDbContext dbContext,
        int docNum,
        CancellationToken cancellationToken)
    {
        if (docNum <= 0)
        {
            return null;
        }

        return await dbContext.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SapDocNum == docNum
                && sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success)
            .OrderByDescending(sale => sale.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Narrows a page of document numbers to those that came from an already-fiscalised sale, in one
    /// query.
    /// </summary>
    public static async Task<HashSet<int>> FindPerSaleDocNumsAsync(
        ApplicationDbContext dbContext,
        IEnumerable<int> docNums,
        CancellationToken cancellationToken)
    {
        var candidates = docNums
            .Where(docNum => docNum > 0)
            .Distinct()
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var matches = await dbContext.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SapDocNum.HasValue
                && sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success
                && candidates.Contains(sale.SapDocNum.Value))
            .Select(sale => sale.SapDocNum!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        return [.. matches];
    }
}
