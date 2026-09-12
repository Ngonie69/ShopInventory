using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// Answers whether a SAP credit memo is the record of a reversal that was already fiscalised, before
/// it reached SAP.
/// </summary>
/// <remarks>
/// The exact counterpart of <see cref="PerSaleInvoiceRegistry"/>, and it exists for the same
/// irreversible reason. A till sale credited at the counter is filed with ZIMRA immediately under the
/// credit's own <c>DCN-</c> number, and its SAP credit memo is raised only when the sale it reverses
/// posts. Every fiscal lookup downstream is keyed on the SAP DocNum — a different number — so without
/// this the memo reads "Unknown", the backfill writes it down as "Not Fiscalised", and the document
/// looks like one that still owes ZIMRA a receipt. Filing a second credit receipt against one return
/// cannot be withdrawn, and as far as ZIMRA is concerned it hands the money back twice.
///
/// The marker is the credit's own row, whose SapDocNum is written in the same SaveChanges that records
/// the post — the same reason the sibling registry reads the sale row rather than the fiscal
/// transaction log, whose write is best-effort and happens after SAP has already committed.
/// </remarks>
internal static class PerSaleCreditNoteRegistry
{
    /// <summary>
    /// Narrows a page of credit memo numbers to those whose receipt was filed before SAP, in one query.
    /// </summary>
    /// <remarks>
    /// Filtered on <see cref="DesktopCreditStatuses.Fiscalised"/>, like the invoice registry is
    /// filtered on a successful fiscalisation: a credit whose fiscal half was refused or is unresolved
    /// genuinely does still owe ZIMRA a receipt, and must keep saying so.
    /// </remarks>
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

        var matches = await dbContext.DesktopCreditNotes
            .AsNoTracking()
            .Where(note => note.SapDocNum.HasValue
                && note.Status == DesktopCreditStatuses.Fiscalised
                && candidates.Contains(note.SapDocNum.Value))
            .Select(note => note.SapDocNum!.Value)
            .ToListAsync(cancellationToken);

        return [.. matches];
    }
}
