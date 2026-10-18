using System.Diagnostics;
using ShopInventory.Common.Sales;

namespace ShopInventory.Services;

/// <summary>
/// Reads booked invoice cost from SAP, one warehouse at a time, over the document range of the invoices
/// asked about.
/// </summary>
/// <remarks>
/// Sequential rather than parallel: the Service Layer client holds one session, and a handful of
/// warehouses is a handful of reads. Each read is timed into the log, because the margin's deadline is
/// what decides whether a report shows it, and the old date-window join missed it on every read without
/// saying how far.
/// </remarks>
public sealed class SapSaleInvoiceCostReader(
    ISAPServiceLayerClient sapClient,
    ILogger<SapSaleInvoiceCostReader> logger) : ISapSaleInvoiceCostReader
{
    public async Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
        IReadOnlyCollection<string> warehouseCodes,
        IReadOnlyCollection<int> docEntries,
        CancellationToken cancellationToken)
    {
        if (docEntries.Count == 0)
        {
            return [];
        }

        var start = docEntries.Min();
        var end = docEntries.Max();
        var lines = new List<SaleInvoiceLineCost>();

        foreach (var warehouse in warehouseCodes
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var timer = Stopwatch.StartNew();
            var read = await sapClient.GetInvoiceLineCostsAsync(warehouse, start, end, cancellationToken);
            logger.LogInformation(
                "Read {Count} invoice cost line(s) for {Warehouse} over DocEntry {Start}-{End} in {Elapsed} ms",
                read.Count, warehouse, start, end, timer.ElapsedMilliseconds);
            lines.AddRange(read);
        }

        var cancelled = await sapClient.GetCancelledInvoiceDocEntriesAsync(start, end, cancellationToken);
        return cancelled.Count == 0
            ? lines
            : lines.Where(line => !cancelled.Contains(line.DocEntry)).ToList();
    }
}
