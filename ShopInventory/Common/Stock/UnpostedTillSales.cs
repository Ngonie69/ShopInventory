using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Stock;

/// <summary>
/// What the ledger has moved that SAP has not been told about yet, per item, for one warehouse.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> Anything that rebuilds or corrects the ledger from SAP has to take this
/// off first, or it hands back units that have already left the shop. The hourly reconciliation used to
/// ask only about the current ledger day, and the 07:00 fetch did not ask at all, so a till sale that
/// had not reached SAP by the morning — a failed fiscalisation, a posting SAP kept refusing — was back on
/// the shelf the next day and stayed there. Sale 1 at KEFSHOP, rung up on 2026-09-09 and still unposted
/// on the 14th, was exactly that.</para>
///
/// <para><b>Two windows, deliberately different.</b> Within the current ledger day every sale not yet
/// <see cref="DesktopSaleConsolidationStatus.Consolidated"/> counts, whatever its source — the rule the
/// reconciliation has always used. Before it, only the till sources count
/// (<see cref="SaleSourceSystems.PostedByDesktopSaleJob"/>): those are the sales known to have taken
/// their units off the ledger and to post one invoice each, so "not Consolidated" really does mean "SAP
/// does not have it". A legacy desktop sale waits for an end-of-day consolidation that may not be
/// running, and counting years of those would empty the shop.</para>
///
/// <para><b>Bounded by <c>lookbackDays</c>.</b> Past it a sale is a person's problem — the exception
/// centre and the end-of-day report both list it — rather than a figure to keep netting for ever.</para>
///
/// <para><b>Credits go the other way.</b> A fiscal credit returns its units to the ledger the moment
/// ZIMRA accepts it, and SAP only hears when the credit memo posts, which waits for the sale. Until then
/// SAP is short by those units, and a reconciliation that ignored them would take the returned stock
/// straight back off the shelf. <see cref="DesktopCreditSapStatuses.ManualInSap"/> is left out on
/// purpose: whether a person has raised that memo is not knowable here, and assuming they have not would
/// put stock on the till that may already be gone.</para>
///
/// <para>A sale whose post went out and whose reply was lost still counts. Counting it twice refuses a
/// sale; not counting it sells stock that is gone, and only the second reaches a customer.</para>
/// </remarks>
public static class UnpostedTillSales
{
    public static async Task<Dictionary<string, decimal>> OutstandingAsync(
        ApplicationDbContext db,
        string warehouseCode,
        DateTime ledgerDay,
        string? fetchTimeCat,
        int lookbackDays,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Bounded by when the sale was captured rather than by its DocDate, which is an accounting date
        // taken from the UTC day and so names the wrong ledger day for anything sold between midnight
        // and the morning fetch. The ledger day runs from the fetch time in CAT, two hours ahead of UTC.
        var dayStart = ledgerDay.Add(StockLedgerDay.ParseFetchTime(fetchTimeCat)).AddHours(-2);
        var dayEnd = dayStart.AddDays(1);
        var earliest = dayStart.AddDays(-Math.Max(0, lookbackDays));
        var tillSources = SaleSourceSystems.PostedByDesktopSaleJob;

        var sold = await db.DesktopSaleLines
            .AsNoTracking()
            .Where(line => line.WarehouseCode == warehouseCode
                        && line.Sale.ConsolidationStatus != DesktopSaleConsolidationStatus.Consolidated
                        && line.Sale.CreatedAt < dayEnd
                        && (line.Sale.CreatedAt >= dayStart
                            || (line.Sale.CreatedAt >= earliest
                                && line.Sale.SourceSystem != null
                                && tillSources.Contains(line.Sale.SourceSystem))))
            .GroupBy(line => line.ItemCode)
            .Select(group => new { ItemCode = group.Key, Quantity = group.Sum(line => line.Quantity) })
            .ToListAsync(cancellationToken);

        var outstanding = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in sold)
        {
            outstanding[line.ItemCode] = outstanding.GetValueOrDefault(line.ItemCode) + line.Quantity;
        }

        var credits = await db.DesktopCreditNotes
            .AsNoTracking()
            .Where(note => note.UnitsReturnedToLedger
                        && (note.SapStatus == DesktopCreditSapStatuses.Deferred
                            || note.SapStatus == DesktopCreditSapStatuses.Failed
                            || note.SapStatus == DesktopCreditSapStatuses.NotRequired)
                        && note.Sale.CreatedAt < dayEnd
                        && (note.Sale.CreatedAt >= dayStart
                            || (note.Sale.CreatedAt >= earliest
                                && note.Sale.SourceSystem != null
                                && tillSources.Contains(note.Sale.SourceSystem))))
            .Select(note => new
            {
                note.Number,
                note.PlanJson,
                SaleWarehouse = note.Sale.WarehouseCode,
                Lines = note.Sale.Lines.Select(line => new { line.LineNum, line.ItemCode, line.WarehouseCode }).ToList()
            })
            .ToListAsync(cancellationToken);

        foreach (var credit in credits)
        {
            List<DesktopCreditQuantity> quantities;

            try
            {
                quantities = JsonSerializer.Deserialize<DesktopCreditPlan>(
                    credit.PlanJson, DesktopCreditNoteService.Json)?.Quantities ?? [];
            }
            catch (JsonException ex)
            {
                // Left out rather than guessed at. The ledger then reads short by this credit, which
                // refuses a sale rather than allowing one.
                logger.LogWarning(ex,
                    "Could not read the plan of credit {CreditNote}; its returned units are not netted", credit.Number);
                continue;
            }

            var byLineNum = credit.Lines.ToDictionary(line => line.LineNum);

            // The same resolution DesktopCreditSapPoster uses when it returned the units, so the two
            // name the same item and warehouse.
            foreach (var credited in quantities)
            {
                if (credited.Quantity <= 0 || !byLineNum.TryGetValue(credited.LineNo, out var line))
                {
                    continue;
                }

                var lineWarehouse = string.IsNullOrWhiteSpace(line.WarehouseCode) ? credit.SaleWarehouse : line.WarehouseCode;

                if (!string.Equals(lineWarehouse, warehouseCode, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(line.ItemCode))
                {
                    continue;
                }

                outstanding[line.ItemCode] = outstanding.GetValueOrDefault(line.ItemCode) - credited.Quantity;
            }
        }

        return outstanding;
    }

    /// <summary>
    /// Takes <paramref name="quantity"/> off the rows, soonest to expire first — the order a sale
    /// deducts in and the order SAP's FEFO allocation picks. A row with no expiry goes last.
    /// </summary>
    /// <returns>The signed change actually made, which is bounded by what the rows held.</returns>
    public static decimal DrawDown(IEnumerable<DailyStockSnapshotItemEntity> rows, decimal quantity)
    {
        var remaining = quantity;

        foreach (var row in rows.OrderBy(row => row.ExpiryDate ?? DateTime.MaxValue))
        {
            if (remaining <= 0)
            {
                break;
            }

            if (row.AvailableQuantity <= 0)
            {
                continue;
            }

            var taken = Math.Min(row.AvailableQuantity, remaining);
            row.AvailableQuantity -= taken;
            remaining -= taken;
        }

        return -(quantity - remaining);
    }
}
