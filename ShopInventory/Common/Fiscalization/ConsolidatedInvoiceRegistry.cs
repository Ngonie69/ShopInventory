using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// Answers whether a SAP invoice is an end-of-day consolidation of sales that were already
/// fiscalised, one receipt at a time, before they reached SAP.
/// </summary>
/// <remarks>
/// Fiscalising such an invoice submits the same economic sales to FDMS a second time, under the
/// consolidated document's number. The platform's duplicate guard is keyed on
/// (TaxPayerTIN, ReceiptType, InvoiceNo), and the two numbers differ, so it cannot see the
/// duplicate; FDMS submission is irreversible, and the only remedy is a manual credit note.
///
/// The row in <see cref="ApplicationDbContext.SaleConsolidations"/> is the marker. It is written in
/// the same SaveChanges that records the SAP DocNum, so it cannot go missing while the consolidated
/// invoice exists — which is why the guards read it rather than the fiscal transaction log, whose
/// write is best-effort and comes after the SAP post has already committed.
/// </remarks>
internal static class ConsolidatedInvoiceRegistry
{
    /// <summary>
    /// Returns the consolidation that produced <paramref name="docNum"/>, or null if that invoice
    /// was not consolidated.
    /// </summary>
    /// <remarks>
    /// Deliberately not filtered by <see cref="ConsolidationStatus"/>. A consolidation that posted
    /// its invoice and then failed on the payment ends up Failed or PartiallyCompleted, but the
    /// receipts inside it are with FDMS either way — what matters is that a SAP DocNum was reached.
    /// </remarks>
    public static async Task<SaleConsolidationEntity?> FindByDocNumAsync(
        ApplicationDbContext dbContext,
        int docNum,
        CancellationToken cancellationToken)
    {
        if (docNum <= 0)
        {
            return null;
        }

        return await dbContext.SaleConsolidations
            .AsNoTracking()
            .Where(consolidation => consolidation.SapDocNum == docNum)
            .OrderByDescending(consolidation => consolidation.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Narrows a page of document numbers to those that came from a consolidation, in one query.
    /// </summary>
    public static async Task<HashSet<int>> FindConsolidatedDocNumsAsync(
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

        var matches = await dbContext.SaleConsolidations
            .AsNoTracking()
            .Where(consolidation => consolidation.SapDocNum.HasValue
                && candidates.Contains(consolidation.SapDocNum.Value))
            .Select(consolidation => consolidation.SapDocNum!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        return [.. matches];
    }
}
