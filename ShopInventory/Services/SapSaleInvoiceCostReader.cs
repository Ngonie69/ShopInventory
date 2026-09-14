using ShopInventory.Common.Sales;

namespace ShopInventory.Services;

/// <summary>
/// Reads booked invoice cost from SAP, one warehouse at a time.
/// </summary>
/// <remarks>
/// Sequential rather than parallel: the Service Layer client holds one session, and a handful of
/// warehouses is a handful of reads.
/// </remarks>
public sealed class SapSaleInvoiceCostReader(ISAPServiceLayerClient sapClient) : ISaleInvoiceCostReader
{
    public async Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
        IReadOnlyCollection<string> warehouseCodes,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var lines = new List<SaleInvoiceLineCost>();

        foreach (var warehouse in warehouseCodes
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lines.AddRange(await sapClient.GetInvoiceLineCostsAsync(warehouse, fromDate, toDate, cancellationToken));
        }

        return lines;
    }
}
