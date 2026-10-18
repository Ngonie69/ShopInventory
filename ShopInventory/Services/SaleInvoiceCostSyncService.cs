using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Copies SAP's booked cost for posted sales' invoices into <see cref="SaleInvoiceCostEntity"/>, so the
/// sales reports read gross profit from this database and never wait on SAP.
/// </summary>
/// <remarks>
/// <para>
/// Every invoice a sale was posted as: a desktop sale's own (shops, vending and offline van sales), a
/// day's consolidated invoice, and an online van sale's confirmed reservation. Newest first, so a
/// sale posted a minute ago is costed on the next run and history fills in behind it.
/// </para>
/// <para>
/// One warehouse at a time over short runs of invoice numbers: SAP is read by range, not by a list,
/// so a range also returns invoices raised in SAP directly from that warehouse, and a short range
/// keeps that overhead small. Each read is bounded, and a run stops after two failures in a row, so
/// a slow Service Layer costs one background slot for a while rather than a queue of them.
/// </para>
/// </remarks>
public sealed class SaleInvoiceCostSyncService(
    ApplicationDbContext db,
    ISapSaleInvoiceCostReader sapReader,
    ILogger<SaleInvoiceCostSyncService> logger)
{
    /// <summary>Invoices considered per run, from each kind of sale.</summary>
    internal const int MaxInvoicesPerRun = 1500;

    /// <summary>How far apart the first and last invoice of one SAP read may be.</summary>
    internal const int MaxDocEntrySpan = 1000;

    /// <summary>Invoices asked for in one SAP read.</summary>
    internal const int MaxInvoicesPerRead = 200;

    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);

    internal static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(4);

    private const int ConsecutiveFailuresToStop = 2;

    /// <summary>Copies what it can in one run; returns how many invoices were copied.</summary>
    public async Task<int> SyncAsync(CancellationToken cancellationToken)
    {
        var pending = await PendingInvoicesAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var started = DateTime.UtcNow;
        var copied = 0;
        var failures = 0;

        foreach (var read in PlanReads(pending))
        {
            if (DateTime.UtcNow - started > RunBudget)
            {
                break;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ReadTimeout);

                var lines = await sapReader.ReadAsync([read.WarehouseCode], read.DocEntries, timeout.Token);
                copied += await SaveAsync(read, lines, cancellationToken);
                failures = 0;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "Could not copy invoice cost from SAP for {Warehouse} over DocEntry {Start}-{End}; it is tried again on the next run",
                    read.WarehouseCode, read.DocEntries[0], read.DocEntries[^1]);
                db.ChangeTracker.Clear();

                if (++failures >= ConsecutiveFailuresToStop)
                {
                    break;
                }
            }
        }

        logger.LogInformation(
            "Copied the booked cost of {Copied} of {Pending} posted invoice(s) from SAP in {Elapsed:0} s",
            copied, pending.Count, (DateTime.UtcNow - started).TotalSeconds);
        return copied;
    }

    /// <summary>A posted sale's invoice whose cost is not copied yet.</summary>
    internal sealed record PendingInvoice(int DocEntry, string WarehouseCode);

    /// <summary>One SAP read: one warehouse, a short ascending run of invoice numbers.</summary>
    internal sealed record PlannedRead(string WarehouseCode, int[] DocEntries);

    private async Task<List<PendingInvoice>> PendingInvoicesAsync(CancellationToken cancellationToken)
    {
        var direct = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SapDocEntry != null
                           && sale.WarehouseCode != ""
                           && !db.SaleInvoiceCosts.Any(cost => cost.DocEntry == sale.SapDocEntry))
            .OrderByDescending(sale => sale.SapDocEntry)
            .Select(sale => new { DocEntry = sale.SapDocEntry!.Value, sale.WarehouseCode })
            .Take(MaxInvoicesPerRun)
            .ToListAsync(cancellationToken);

        var consolidated = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SapDocEntry == null
                           && sale.Consolidation != null
                           && sale.Consolidation.SapDocEntry != null
                           && sale.WarehouseCode != ""
                           && !db.SaleInvoiceCosts.Any(cost => cost.DocEntry == sale.Consolidation.SapDocEntry))
            .Select(sale => new { DocEntry = sale.Consolidation!.SapDocEntry!.Value, sale.WarehouseCode })
            .Distinct()
            .OrderByDescending(invoice => invoice.DocEntry)
            .Take(MaxInvoicesPerRun)
            .ToListAsync(cancellationToken);

        // The warehouse is the first line's, as the van analysis files the sale under it.
        var vans = await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.SourceSystem == SaleSourceSystems.VanSales
                                  && reservation.Status == ReservationStatus.Confirmed
                                  && reservation.SAPDocEntry != null
                                  && !db.SaleInvoiceCosts.Any(cost => cost.DocEntry == reservation.SAPDocEntry))
            .OrderByDescending(reservation => reservation.SAPDocEntry)
            .Select(reservation => new
            {
                DocEntry = reservation.SAPDocEntry!.Value,
                WarehouseCode = reservation.Lines
                    .OrderBy(line => line.LineNum)
                    .Select(line => line.WarehouseCode)
                    .FirstOrDefault()
            })
            .Take(MaxInvoicesPerRun)
            .ToListAsync(cancellationToken);

        return direct.Select(invoice => new PendingInvoice(invoice.DocEntry, invoice.WarehouseCode))
            .Concat(consolidated.Select(invoice => new PendingInvoice(invoice.DocEntry, invoice.WarehouseCode)))
            .Concat(vans.Select(invoice => new PendingInvoice(invoice.DocEntry, invoice.WarehouseCode ?? string.Empty)))
            .Select(invoice => invoice with { WarehouseCode = invoice.WarehouseCode.Trim() })
            .Where(invoice => invoice.WarehouseCode.Length > 0)
            .DistinctBy(invoice => (invoice.DocEntry, invoice.WarehouseCode.ToUpperInvariant()))
            .ToList();
    }

    /// <summary>
    /// Splits the pending invoices into reads: per warehouse, newest runs first, each run at most
    /// <see cref="MaxDocEntrySpan"/> wide and <see cref="MaxInvoicesPerRead"/> long.
    /// </summary>
    internal static IEnumerable<PlannedRead> PlanReads(IEnumerable<PendingInvoice> pending)
    {
        var reads = new List<PlannedRead>();
        foreach (var warehouse in pending.GroupBy(invoice => invoice.WarehouseCode, StringComparer.OrdinalIgnoreCase))
        {
            var run = new List<int>();
            foreach (var docEntry in warehouse.Select(invoice => invoice.DocEntry).Distinct().Order())
            {
                if (run.Count > 0 && (docEntry - run[0] > MaxDocEntrySpan || run.Count == MaxInvoicesPerRead))
                {
                    reads.Add(new PlannedRead(warehouse.Key, [.. run]));
                    run.Clear();
                }

                run.Add(docEntry);
            }

            if (run.Count > 0)
            {
                reads.Add(new PlannedRead(warehouse.Key, [.. run]));
            }
        }

        return reads.OrderByDescending(read => read.DocEntries[^1]);
    }

    private async Task<int> SaveAsync(
        PlannedRead read, IReadOnlyList<SaleInvoiceLineCost> lines, CancellationToken cancellationToken)
    {
        // SAP filtered the lines to this warehouse; the range also held invoices that are not ours.
        var byDoc = lines.ToLookup(line => line.DocEntry);
        var readAt = DateTime.UtcNow;

        foreach (var docEntry in read.DocEntries)
        {
            db.SaleInvoiceCosts.Add(new SaleInvoiceCostEntity
            {
                DocEntry = docEntry,
                WarehouseCode = read.WarehouseCode,
                ReadAt = readAt,
                Lines = byDoc[docEntry]
                    .Select(line => new SaleInvoiceCostLineEntity
                    {
                        ItemCode = line.ItemCode,
                        Quantity = line.Quantity,
                        Revenue = line.Revenue,
                        GrossProfit = line.GrossProfit
                    })
                    .ToList()
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return read.DocEntries.Length;
    }
}
