using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// The two orders a desktop sale's lines leave this API in, and how a fiscal receipt line is found
/// among them.
/// </summary>
/// <remarks>
/// <para>
/// <c>LineNum</c> is not a key. <c>CreateDesktopSaleHandler</c> used to keep a till's own number when it
/// was positive and give position + 1 otherwise, so a till numbering from zero stored 0,1,2 as 1,1,2 —
/// INV1753 (19 September 2026) has two lines numbered 1, and every credit step that keyed a dictionary
/// on <c>LineNum</c> threw "An item with the same key has already been added. Key: 1" instead of
/// posting its SAP credit memo.
/// </para>
/// <para>
/// What is stable is position. The receipt is filed in <see cref="Receipt"/> order with <c>HH</c> =
/// position + 1 (see <c>RevmaxFiscalizationService.BuildItems</c>), which is also what a credit plan's
/// <c>LineNo</c> reads back as; the SAP invoice is built in <see cref="Invoice"/> order, and a memo's
/// <c>BaseLine</c> is the index in that. Both orders break ties by <c>Id</c>, which is the order EF
/// already loaded them in, so neither changes for a sale that was filed or posted before this.
/// </para>
/// </remarks>
public static class DesktopSaleLineOrder
{
    /// <summary>The order the fiscal receipt was filed in: the order the lines were taken.</summary>
    public static List<DesktopSaleLineEntity> Receipt(IEnumerable<DesktopSaleLineEntity> lines) =>
        lines.OrderBy(line => line.Id).ToList();

    /// <summary>The order the SAP invoice's lines are in; a memo's <c>BaseLine</c> indexes this.</summary>
    public static List<DesktopSaleLineEntity> Invoice(IEnumerable<DesktopSaleLineEntity> lines) =>
        lines.OrderBy(line => line.LineNum).ThenBy(line => line.Id).ToList();

    /// <summary>The sale line a fiscal receipt line (1-based) was filed from, or null.</summary>
    public static DesktopSaleLineEntity? ForReceiptLine(IEnumerable<DesktopSaleLineEntity> lines, int receiptLineNo)
    {
        var receipt = Receipt(lines);
        return receiptLineNo >= 1 && receiptLineNo <= receipt.Count ? receipt[receiptLineNo - 1] : null;
    }
}
