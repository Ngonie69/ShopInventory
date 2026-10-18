using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;

namespace ShopInventory.Services;

/// <summary>
/// The reports' booked invoice cost, from the copy <see cref="SaleInvoiceCostSyncService"/> keeps.
/// </summary>
/// <remarks>
/// Exact invoices, not a range: this database takes a list of numbers where SAP's SQLQueries could not.
/// An invoice the copy has not reached yet has no lines here, so its sales count as not yet costed —
/// the same as a sale not yet in SAP — and gain their profit on a later load.
/// </remarks>
public sealed class StoredSaleInvoiceCostReader(ApplicationDbContext db) : ISaleInvoiceCostReader
{
    // Well inside what PostgreSQL takes as one array parameter, and keeps each statement small.
    private const int ChunkSize = 2000;

    public async Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
        IReadOnlyCollection<string> warehouseCodes,
        IReadOnlyCollection<int> docEntries,
        CancellationToken cancellationToken)
    {
        var warehouses = warehouseCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (warehouses.Count == 0 || docEntries.Count == 0)
        {
            return [];
        }

        var lines = new List<SaleInvoiceLineCost>();
        foreach (var chunk in docEntries.Distinct().Chunk(ChunkSize))
        {
            lines.AddRange(await db.SaleInvoiceCostLines
                .AsNoTracking()
                .Where(line => chunk.Contains(line.Invoice!.DocEntry) && warehouses.Contains(line.Invoice.WarehouseCode))
                .Select(line => new SaleInvoiceLineCost(
                    line.Invoice!.DocEntry,
                    line.ItemCode,
                    line.Invoice.WarehouseCode,
                    line.Quantity,
                    line.Revenue,
                    line.GrossProfit))
                .ToListAsync(cancellationToken));
        }

        return lines;
    }
}
