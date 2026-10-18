namespace ShopInventory.Common.Sales;

/// <summary>
/// One SAP invoice line with the cost SAP booked for it.
/// </summary>
/// <param name="DocEntry">The invoice — what a posted sale's <c>SapDocEntry</c>, or its consolidation's, points at.</param>
/// <param name="ItemCode">The item on the line.</param>
/// <param name="WarehouseCode">The warehouse the line's stock left.</param>
/// <param name="Quantity">The line's quantity, in the unit it was invoiced in.</param>
/// <param name="Revenue">The line before VAT, in the invoice's own currency.</param>
/// <param name="GrossProfit">
/// SAP's gross profit on the line, in the invoice's own currency: the revenue less the stock value SAP
/// relieved when the goods left, at that moment's cost. So a cost change after the sale does not move it.
/// </param>
public sealed record SaleInvoiceLineCost(
    int DocEntry,
    string ItemCode,
    string WarehouseCode,
    decimal Quantity,
    decimal Revenue,
    decimal GrossProfit);

/// <summary>
/// Reads the cost SAP booked on the invoices the desktop sales were posted as.
/// </summary>
/// <remarks>
/// An interface so the report can be tested without SAP, and so a report whose SAP read fails still has
/// everything else to say: the caller catches, and shows the margin as unavailable.
/// </remarks>
public interface ISaleInvoiceCostReader
{
    /// <summary>
    /// The non-cancelled lines out of <paramref name="warehouseCodes"/> on the invoices numbered from the
    /// lowest to the highest of <paramref name="docEntries"/>.
    /// </summary>
    /// <remarks>
    /// Read per warehouse over the range rather than by the numbers themselves, because a statement cannot
    /// take a list of numbers as a parameter. The range holds other invoices too — a depot's warehouse is
    /// also invoiced from SAP directly — so the caller keeps only the lines of the invoices it posted.
    /// </remarks>
    Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
        IReadOnlyCollection<string> warehouseCodes,
        IReadOnlyCollection<int> docEntries,
        CancellationToken cancellationToken);
}
