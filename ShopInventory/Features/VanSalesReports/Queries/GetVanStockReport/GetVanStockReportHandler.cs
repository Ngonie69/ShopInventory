using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanStockReport;

/// <summary>
/// Builds the van stock report: each morning's count against the SAP documents created since the
/// count before it, each trading day's sales against the SAP invoices dated that day, and the load,
/// dead stock and expiry figures read from the counts.
/// </summary>
/// <remarks>
/// The counts are the snapshot's <c>OriginalQuantity</c>: SAP's own book stock read at about 07:00.
/// <c>AvailableQuantity</c> on a van warehouse means nothing, since no van sales path moves it.
///
/// Snapshot items are per batch, so everything here sums across batches to reach an item.
/// </remarks>
public sealed class GetVanStockReportHandler(
    ApplicationDbContext db,
    IVanStockSapDocuments sapDocuments,
    IOptions<DailyStockSettings> dailyStockSettings
) : IRequestHandler<GetVanStockReportQuery, ErrorOr<VanStockReportResult>>
{
    /// <summary>How many unexplained items a morning names before it stops listing them.</summary>
    private const int UnexplainedPerMorning = 25;

    /// <summary>How many differing items a trading day names before it stops listing them.</summary>
    private const int DifferencesPerSalesDay = 25;

    /// <summary>Batches inside this window are worth a buyer's attention; beyond it they are not yet news.</summary>
    private const int ExpiryHorizonDays = 60;

    /// <summary>
    /// How long after the period a late invoice is still looked for. Beyond it a day's sales count as
    /// not in SAP, and reading further would page through every invoice the account has had since.
    /// </summary>
    private const int LatePostingLookaheadDays = 14;

    /// <summary>Rounding floor for a quantity difference, below which it is float noise.</summary>
    private const decimal Epsilon = 0.001m;

    private const int CommentLength = 200;

    public async Task<ErrorOr<VanStockReportResult>> Handle(
        GetVanStockReportQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (to < from)
        {
            return Error.Validation(
                "VanSalesReports.InvalidRange",
                "The end of the period cannot be before its start.");
        }

        if ((to - from).TotalDays > VanSalesFacts.MaximumDays)
        {
            return Error.Validation(
                "VanSalesReports.RangeTooWide",
                $"Choose a period of {VanSalesFacts.MaximumDays} days or fewer.");
        }

        var vanAccounts = await LoadVanWarehousesAsync(query, cancellationToken);

        if (vanAccounts.Count == 0)
        {
            return Error.Validation(
                "VanSalesReports.NoVans",
                "No van warehouse is assigned to any rep, so there is no stock to reconcile.");
        }

        var vans = new HashSet<string>(vanAccounts.Keys, StringComparer.OrdinalIgnoreCase);
        var fetchTime = StockLedgerDay.ParseFetchTime(dailyStockSettings.Value.StockFetchTimeCAT);
        var nowCat = AuditService.ToCAT(DateTime.UtcNow);
        var todayCat = nowCat.Date;

        var snapshots = await LoadSnapshotsAsync(vans, from, to, fetchTime, cancellationToken);
        var adjustments = await LoadAdjustmentsAsync(vans, from, to, cancellationToken);
        var sightings = await LoadTransferSightingsAsync(vans, from, cancellationToken);

        // Sales are read through the shared fact reader like every other van report, then attributed
        // to a warehouse by the line rather than the document — a line carries the warehouse it came
        // off, and a reservation header carries none at all.
        var lines = (await VanSalesFactReader.LoadSaleLinesAsync(
                db, new VanSalesFactFilter(from, to), cancellationToken))
            .Where(line => line.WarehouseCode is not null && vans.Contains(line.WarehouseCode))
            .ToList();

        // Whatever account a van's sales actually carried is read too, so an account missing from the
        // rep's profile does not hide that van's invoices.
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line.VanAccountCode))
            {
                vanAccounts[line.WarehouseCode!.Trim()].Add(line.VanAccountCode.Trim());
            }
        }

        var sap = await sapDocuments.LoadAsync(
            vanAccounts.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyCollection<string>)pair.Value.ToList(),
                StringComparer.OrdinalIgnoreCase),
            from,
            Min(todayCat, to.AddDays(LatePostingLookaheadDays)),
            cancellationToken);

        var sold = lines
            .GroupBy(line => new StockKey(line.WarehouseCode!.ToUpperInvariant(), line.TradingDate, line.ItemCode.ToUpperInvariant()))
            .ToDictionary(group => group.Key, group => group.Sum(StockQuantity));

        var latest = snapshots.Count == 0 ? (DateTime?)null : snapshots.Max(s => s.SnapshotDate);
        var latestCountAt = snapshots.Count == 0 ? (DateTime?)null : snapshots.Max(s => s.CountedAt);

        var days = BuildDays(snapshots, sold, adjustments);
        var mornings = BuildMornings(snapshots, sap, sightings);
        var salesDays = BuildSalesDays(vans, snapshots, lines, sap, from, to, fetchTime, nowCat);
        var items = BuildItems(snapshots, sold);

        return new VanStockReportResult(
            FromDate: from,
            ToDate: to,
            DeadStockDays: query.DeadStockDays,
            Summary: BuildSummary(vans, days, items, snapshots, latest, latestCountAt, todayCat, query.DeadStockDays, sap.Available),
            Days: days,
            Mornings: mornings,
            SalesDays: salesDays,
            Items: items,
            Expiring: BuildExpiring(snapshots, todayCat),
            Quality: BuildQuality(vans, vanAccounts, snapshots, lines, latest, todayCat, sap),
            Vans: BuildVans(vanAccounts, snapshots, days, sold, mornings, salesDays, query.DeadStockDays));
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The warehouses that are vans — a rep is assigned to them and a depot supplies them — each with
    /// the business partner its rep's sales invoice to. Read from the assignment rather than a code
    /// prefix: see the replenishment report for why.
    /// </summary>
    private async Task<Dictionary<string, HashSet<string>>> LoadVanWarehousesAsync(
        GetVanStockReportQuery query,
        CancellationToken cancellationToken)
    {
        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.SupplyingWarehouseCode != null && user.AssignedWarehouseCodes != null)
            .ToListAsync(cancellationToken);

        var vans = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            foreach (var code in user.GetWarehouseCodes().Where(code => !string.IsNullOrWhiteSpace(code)))
            {
                if (!vans.TryGetValue(code.Trim(), out var accounts))
                {
                    accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    vans[code.Trim()] = accounts;
                }

                if (!string.IsNullOrWhiteSpace(user.AssignedBusinessPartnerCode))
                {
                    accounts.Add(user.AssignedBusinessPartnerCode.Trim());
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(query.VanWarehouseCode))
        {
            var wanted = query.VanWarehouseCode.Trim();
            vans = vans
                .Where(pair => string.Equals(pair.Key, wanted, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        return vans;
    }

    private async Task<List<SnapshotRow>> LoadSnapshotsAsync(
        HashSet<string> vans,
        DateTime from,
        DateTime to,
        TimeSpan fetchTime,
        CancellationToken cancellationToken)
    {
        var headers = await db.DailyStockSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.SnapshotDate >= from
                               && snapshot.SnapshotDate <= to
                               && vans.Contains(snapshot.WarehouseCode))
            .Select(snapshot => new
            {
                snapshot.WarehouseCode,
                snapshot.SnapshotDate,
                snapshot.Status,
                snapshot.CompletedAt,
                snapshot.UnbatchedStockMissing,
                Items = snapshot.Items
                    .Select(item => new
                    {
                        item.ItemCode,
                        item.ItemDescription,
                        item.BatchNumber,
                        item.OriginalQuantity,
                        item.ExpiryDate
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return headers
            .Select(header =>
            {
                var date = header.SnapshotDate.Date;

                // When the count actually read SAP. The completion time, unless it is missing or lands
                // on another day (a header rewritten by hand); then the scheduled fetch time, which is
                // when every count is meant to run.
                var completedCat = header.CompletedAt is { } completed
                    ? AuditService.ToCAT(DateTime.SpecifyKind(completed, DateTimeKind.Utc))
                    : (DateTime?)null;
                var countedAt = completedCat is { } at && at.Date == date ? at : date + fetchTime;

                return new SnapshotRow(
                    header.WarehouseCode.Trim().ToUpperInvariant(),
                    date,
                    countedAt,
                    header.Status == StockSnapshotStatus.Complete && !header.UnbatchedStockMissing,
                    header.Items
                        .Select(item => new SnapshotItem(
                            item.ItemCode.ToUpperInvariant(),
                            item.ItemDescription,
                            item.BatchNumber,
                            item.OriginalQuantity,
                            item.ExpiryDate))
                        .ToList());
            })
            .ToList();
    }

    private async Task<Dictionary<StockKey, decimal>> LoadAdjustmentsAsync(
        HashSet<string> vans,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var rows = await db.StockTransferAdjustments
            .AsNoTracking()
            .Where(adjustment => adjustment.SnapshotDate >= from
                                 && adjustment.SnapshotDate <= to
                                 && vans.Contains(adjustment.WarehouseCode))
            .Select(adjustment => new
            {
                adjustment.WarehouseCode,
                adjustment.SnapshotDate,
                adjustment.ItemCode,
                adjustment.AdjustmentQuantity
            })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => new StockKey(row.WarehouseCode.ToUpperInvariant(), row.SnapshotDate.Date, row.ItemCode.ToUpperInvariant()))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.AdjustmentQuantity));
    }

    /// <summary>
    /// When TransferEventListener first saw each SAP transfer reach or leave a van, on the CAT clock.
    /// </summary>
    /// <remarks>
    /// SAP gives a transfer a creation date and no time, so on its own a transfer keyed in at 06:30
    /// cannot be told from one keyed in at 16:00 — and the 07:00 count falls between them. The
    /// listener polls SAP through the day, so the moment it detected the document is the closest thing
    /// to a creation time there is.
    /// </remarks>
    private async Task<Dictionary<(string Warehouse, int DocEntry), DateTime>> LoadTransferSightingsAsync(
        HashSet<string> vans,
        DateTime from,
        CancellationToken cancellationToken)
    {
        var rows = await db.StockTransferAdjustments
            .AsNoTracking()
            .Where(adjustment => adjustment.SnapshotDate >= from.AddDays(-1)
                                 && adjustment.TransferDocEntry != null
                                 && vans.Contains(adjustment.WarehouseCode))
            .Select(adjustment => new
            {
                adjustment.WarehouseCode,
                adjustment.TransferDocEntry,
                adjustment.DetectedAt
            })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => (row.WarehouseCode.Trim().ToUpperInvariant(), row.TransferDocEntry!.Value))
            .ToDictionary(
                group => group.Key,
                group => AuditService.ToCAT(DateTime.SpecifyKind(group.Min(row => row.DetectedAt), DateTimeKind.Utc)));
    }

    // ── C1: the day ─────────────────────────────────────────────────────────────

    private static List<VanStockDayResult> BuildDays(
        List<SnapshotRow> snapshots,
        Dictionary<StockKey, decimal> sold,
        Dictionary<StockKey, decimal> adjustments) =>
        snapshots
            .Select(snapshot =>
            {
                var byItem = SumByItem(snapshot);

                decimal SoldFor(string itemCode) =>
                    sold.TryGetValue(new StockKey(snapshot.WarehouseCode, snapshot.SnapshotDate, itemCode), out var q)
                        ? q
                        : 0m;

                var soldItems = byItem.Keys.Count(itemCode => SoldFor(itemCode) > 0);

                return new VanStockDayResult(
                    VanWarehouseCode: snapshot.WarehouseCode,
                    SnapshotDate: snapshot.SnapshotDate,
                    SnapshotComplete: snapshot.IsComplete,
                    ItemCount: byItem.Count,
                    LoadedQuantity: byItem.Values.Sum(),
                    SoldQuantity: byItem.Keys.Sum(SoldFor),
                    AdjustmentQuantity: byItem.Keys.Sum(itemCode =>
                        adjustments.TryGetValue(
                            new StockKey(snapshot.WarehouseCode, snapshot.SnapshotDate, itemCode), out var q)
                            ? q
                            : 0m),
                    SoldItemCount: soldItems,
                    UnsoldItemCount: byItem.Count - soldItems);
            })
            .OrderBy(day => day.VanWarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(day => day.SnapshotDate)
            .ToList();

    // ── C2: each morning against SAP's documents ─────────────────────────────────

    /// <summary>
    /// Each count against the one before it, explained by the SAP documents created in between.
    /// </summary>
    /// <remarks>
    /// Only complete counts are chained. An unfinished one is missing items, and comparing against
    /// it would name every missing item as a movement no document explains. A missing or unfinished
    /// count does not break the chain, though: the two counts either side are compared over the
    /// longer window, since the documents cover every day in it.
    /// </remarks>
    private static List<VanStockMorningResult> BuildMornings(
        List<SnapshotRow> snapshots,
        VanStockSapRead sap,
        Dictionary<(string Warehouse, int DocEntry), DateTime> sightings)
    {
        var placed = sap.Movements
            .Select(movement => (Movement: movement, At: PlacedAt(movement, sightings)))
            .GroupBy(entry => entry.Movement.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var mornings = new List<VanStockMorningResult>();

        foreach (var van in snapshots.Where(s => s.IsComplete).GroupBy(s => s.WarehouseCode, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = van.OrderBy(snapshot => snapshot.SnapshotDate).ToList();
            var vanMovements = placed.GetValueOrDefault(van.Key) ?? [];

            for (var index = 1; index < ordered.Count; index++)
            {
                var opening = ordered[index - 1];
                var closing = ordered[index];
                var openingByItem = SumByItem(opening);
                var closingByItem = SumByItem(closing);

                var inWindow = vanMovements
                    .Where(entry => entry.At > opening.CountedAt && entry.At <= closing.CountedAt)
                    .ToList();

                var byItem = inWindow
                    .GroupBy(entry => entry.Movement.ItemCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Select(entry => entry.Movement).ToList(),
                        StringComparer.OrdinalIgnoreCase);

                var itemCodes = openingByItem.Keys
                    .Union(closingByItem.Keys, StringComparer.OrdinalIgnoreCase)
                    .Union(byItem.Keys, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var unexplained = sap.Available
                    ? itemCodes
                        .Select(item =>
                        {
                            var moves = byItem.GetValueOrDefault(item) ?? [];

                            decimal Total(VanStockDocumentKind kind) =>
                                Math.Abs(moves.Where(m => m.Kind == kind).Sum(m => m.Quantity));

                            // A cancelled transfer arrives as the same transfer with negative
                            // quantities, so transfers are netted by sign rather than by kind.
                            var transferIn = moves.Where(m => m.Kind == VanStockDocumentKind.TransferIn).Sum(m => m.Quantity);
                            var transferOut = -moves.Where(m => m.Kind == VanStockDocumentKind.TransferOut).Sum(m => m.Quantity);

                            return new VanStockItemMovementResult(
                                ItemCode: item,
                                ItemDescription: DescriptionOf(opening, closing, moves, item),
                                Opening: openingByItem.GetValueOrDefault(item),
                                Invoiced: Total(VanStockDocumentKind.Invoice),
                                Credited: Total(VanStockDocumentKind.CreditNote),
                                TransferredIn: transferIn,
                                TransferredOut: transferOut,
                                Closing: closingByItem.GetValueOrDefault(item));
                        })
                        .Where(item => Math.Abs(item.Unexplained) > Epsilon)
                        .OrderByDescending(item => Math.Abs(item.Unexplained))
                        .ToList()
                    : [];

                var documents = inWindow
                    .GroupBy(entry => (entry.Movement.Kind, entry.Movement.DocEntry))
                    .Select(group =>
                    {
                        var first = group.First();
                        var seen = first.Movement.CreatedTimeKnown || first.At != first.Movement.CreatedAt;

                        return new VanStockDocumentResult(
                            Kind: first.Movement.Kind.ToString(),
                            DocEntry: first.Movement.DocEntry,
                            DocNum: first.Movement.DocNum,
                            DocDate: first.Movement.DocDate,
                            CreatedAt: first.At,
                            CreatedTimeKnown: seen,
                            ItemCount: group.Select(entry => entry.Movement.ItemCode)
                                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                            Comments: Shorten(first.Movement.Comments));
                    })
                    .OrderBy(document => document.CreatedAt)
                    .ThenBy(document => document.DocNum)
                    .ToList();

                mornings.Add(new VanStockMorningResult(
                    VanWarehouseCode: van.Key,
                    FromSnapshot: opening.SnapshotDate,
                    ToSnapshot: closing.SnapshotDate,
                    CountedFrom: opening.CountedAt,
                    CountedTo: closing.CountedAt,
                    GapDays: (int)(closing.SnapshotDate - opening.SnapshotDate).TotalDays,
                    SapChecked: sap.Available,
                    ItemCount: itemCodes.Count,
                    ItemsMoved: byItem.Count,
                    ItemsUnexplained: unexplained.Count,
                    Documents: documents,
                    Unexplained: unexplained.Take(UnexplainedPerMorning).ToList()));
            }
        }

        return mornings
            .OrderBy(morning => morning.VanWarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(morning => morning.ToSnapshot)
            .ToList();
    }

    /// <summary>
    /// Where a movement falls against the counts: its creation time when SAP has one, else the moment
    /// the listener saw the transfer, else midday on the day SAP created it.
    /// </summary>
    private static DateTime PlacedAt(
        VanStockSapMovement movement,
        Dictionary<(string Warehouse, int DocEntry), DateTime> sightings) =>
        movement.CreatedTimeKnown
            ? movement.CreatedAt
            : sightings.TryGetValue((movement.WarehouseCode, movement.DocEntry), out var seen)
              && seen.Date == movement.CreatedAt.Date
                ? seen
                : movement.CreatedAt;

    // ── Sales against SAP invoices ──────────────────────────────────────────────

    /// <summary>
    /// Every van's trading days that recorded a sale or carry a SAP invoice, each judged on whether
    /// SAP had the day's sales before the next morning's count.
    /// </summary>
    private static List<VanStockSalesDayResult> BuildSalesDays(
        HashSet<string> vans,
        List<SnapshotRow> snapshots,
        List<VanSaleLineFact> lines,
        VanStockSapRead sap,
        DateTime from,
        DateTime to,
        TimeSpan fetchTime,
        DateTime nowCat)
    {
        var countAt = snapshots
            .GroupBy(snapshot => (snapshot.WarehouseCode, snapshot.SnapshotDate))
            .ToDictionary(group => group.Key, group => group.Max(snapshot => snapshot.CountedAt));

        var recorded = lines
            .GroupBy(line => (Van: line.WarehouseCode!.ToUpperInvariant(), Day: line.TradingDate.Date))
            .ToDictionary(group => group.Key, group => group.ToList());

        var invoiced = sap.Movements
            .Where(movement => movement.Kind == VanStockDocumentKind.Invoice
                               && movement.DocDate.Date >= from
                               && movement.DocDate.Date <= to)
            .GroupBy(movement => (Van: movement.WarehouseCode.ToUpperInvariant(), Day: movement.DocDate.Date))
            .ToDictionary(group => group.Key, group => group.ToList());

        var results = new List<VanStockSalesDayResult>();

        foreach (var key in recorded.Keys.Union(invoiced.Keys).Where(key => vans.Contains(key.Van)))
        {
            var ownLines = recorded.GetValueOrDefault(key) ?? [];
            var ownInvoices = invoiced.GetValueOrDefault(key) ?? [];

            var recordedByItem = ownLines
                .GroupBy(line => line.ItemCode.ToUpperInvariant())
                .ToDictionary(group => group.Key, group => (Quantity: group.Sum(StockQuantity), Name: group.First().ItemDescription));

            var invoicedByItem = ownInvoices
                .GroupBy(movement => movement.ItemCode)
                .ToDictionary(group => group.Key, group => (Quantity: -group.Sum(m => m.Quantity), Name: group.First().ItemDescription));

            var differences = recordedByItem.Keys
                .Union(invoicedByItem.Keys)
                .Select(item => new VanStockSalesItemResult(
                    item,
                    recordedByItem.TryGetValue(item, out var r) ? r.Name : invoicedByItem[item].Name,
                    recordedByItem.GetValueOrDefault(item).Quantity,
                    invoicedByItem.GetValueOrDefault(item).Quantity))
                .Where(item => Math.Abs(item.Difference) > Epsilon)
                .OrderByDescending(item => Math.Abs(item.Difference))
                .ToList();

            var nextCountAt = countAt.TryGetValue((key.Van, key.Day.AddDays(1)), out var counted)
                ? counted
                : key.Day.AddDays(1) + fetchTime;

            var recordedItems = recordedByItem.Count(pair => pair.Value.Quantity > Epsilon);
            var sapItems = invoicedByItem.Count(pair => pair.Value.Quantity > Epsilon);
            var notInSap = differences.Count(item => item.Difference < 0);
            var onlyInSap = differences.Count(item => item.Difference > 0);
            DateTime? first = ownInvoices.Count == 0 ? null : ownInvoices.Min(m => m.CreatedAt);
            DateTime? last = ownInvoices.Count == 0 ? null : ownInvoices.Max(m => m.CreatedAt);

            int? daysLate = null;
            string status;

            if (!sap.Available)
            {
                status = "Unchecked";
            }
            else if (recordedItems == 0 && sapItems > 0)
            {
                status = "SapOnly";
            }
            else if (notInSap > 0)
            {
                // Before the next count nothing is late yet: the day's invoice is still due.
                status = nowCat < nextCountAt ? "Pending" : "NotInSap";
            }
            else if (last is { } lastAt && lastAt > nextCountAt)
            {
                status = "Late";
                daysLate = Math.Max(1, (int)(lastAt.Date - key.Day).TotalDays);
            }
            else
            {
                status = "OnTime";
            }

            results.Add(new VanStockSalesDayResult(
                VanWarehouseCode: key.Van,
                TradingDate: key.Day,
                Status: status,
                RecordedItems: recordedItems,
                SapItems: sapItems,
                SapInvoiceCount: ownInvoices.Select(m => m.DocEntry).Distinct().Count(),
                FirstInvoicedAt: first,
                LastInvoicedAt: last,
                NextCountAt: nextCountAt,
                DaysLate: daysLate,
                ItemsNotInSap: sap.Available ? notInSap : 0,
                ItemsOnlyInSap: sap.Available ? onlyInSap : 0,
                Differences: sap.Available ? differences.Take(DifferencesPerSalesDay).ToList() : []));
        }

        return results
            .OrderBy(day => day.VanWarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(day => day.TradingDate)
            .ToList();
    }

    // ── Per van ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One row per van, every van included — one with no snapshot in the period still gets a row, so
    /// the page can say it was never counted rather than leaving it out.
    /// </summary>
    private static List<VanStockVanResult> BuildVans(
        Dictionary<string, HashSet<string>> vanAccounts,
        List<SnapshotRow> snapshots,
        List<VanStockDayResult> days,
        Dictionary<StockKey, decimal> sold,
        List<VanStockMorningResult> mornings,
        List<VanStockSalesDayResult> salesDays,
        int deadStockDays) =>
        vanAccounts
            .Select(pair =>
            {
                var van = pair.Key.ToUpperInvariant();
                var own = snapshots
                    .Where(snapshot => string.Equals(snapshot.WarehouseCode, van, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var itemMornings = own
                    .SelectMany(snapshot => SumByItem(snapshot).Keys
                        .Select(item => new
                        {
                            Item = item,
                            Sold = sold.TryGetValue(new StockKey(snapshot.WarehouseCode, snapshot.SnapshotDate, item), out var q)
                                ? q
                                : 0m
                        }))
                    .ToList();

                var ownDays = days
                    .Where(day => string.Equals(day.VanWarehouseCode, van, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var ownMornings = mornings
                    .Where(morning => string.Equals(morning.VanWarehouseCode, van, StringComparison.OrdinalIgnoreCase)
                                      && morning.SapChecked)
                    .ToList();

                var ownSales = salesDays
                    .Where(day => string.Equals(day.VanWarehouseCode, van, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return new VanStockVanResult(
                    VanWarehouseCode: van,
                    DaysCounted: ownDays.Count,
                    DaysWithSales: ownDays.Count(day => day.SoldQuantity > 0),
                    ItemCount: itemMornings.Select(row => row.Item).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    ItemDays: itemMornings.Count,
                    SoldItemDays: itemMornings.Count(row => row.Sold > 0),
                    DeadItemCount: itemMornings
                        .GroupBy(row => row.Item, StringComparer.OrdinalIgnoreCase)
                        .Count(item => item.Count() >= deadStockDays && item.All(row => row.Sold == 0)),
                    AccountCodes: pair.Value.OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToList(),
                    MorningsChecked: ownMornings.Count,
                    MorningsTied: ownMornings.Count(morning => morning.TiesToSap),
                    SalesDays: ownSales.Count(day => day.Status != "Unchecked"),
                    SalesDaysOnTime: ownSales.Count(day => day.Status == "OnTime"),
                    SalesDaysLate: ownSales.Count(day => day.Status == "Late"),
                    SalesDaysNotInSap: ownSales.Count(day => day.Status == "NotInSap"),
                    SalesDaysSapOnly: ownSales.Count(day => day.Status == "SapOnly"),
                    MaxDaysLate: ownSales.Max(day => day.DaysLate));
            })
            .OrderBy(van => van.VanWarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── C3 / C4: sell-through and dead stock ────────────────────────────────────

    private static List<VanStockItemResult> BuildItems(
        List<SnapshotRow> snapshots,
        Dictionary<StockKey, decimal> sold) =>
        snapshots
            .SelectMany(snapshot => SumByItem(snapshot)
                .Select(item => new
                {
                    snapshot.WarehouseCode,
                    snapshot.SnapshotDate,
                    ItemCode = item.Key,
                    Loaded = item.Value,
                    Description = snapshot.Items
                        .FirstOrDefault(row => row.ItemCode == item.Key)?.ItemDescription,
                    Sold = sold.TryGetValue(
                        new StockKey(snapshot.WarehouseCode, snapshot.SnapshotDate, item.Key), out var q)
                        ? q
                        : 0m
                }))
            .GroupBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanStockItemResult(
                ItemCode: group.Key,
                ItemDescription: group
                    .Select(row => row.Description)
                    .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description)),
                VanCount: group.Select(row => row.WarehouseCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                DaysOnVan: group.Count(),
                DaysSold: group.Count(row => row.Sold > 0),
                // The dead-stock measure: loaded, carried around, and sold nothing.
                DaysOnVanWithoutSelling: group.Count(row => row.Sold == 0),
                LoadedQuantity: group.Sum(row => row.Loaded),
                SoldQuantity: group.Sum(row => row.Sold),
                LastSoldOn: group.Where(row => row.Sold > 0)
                    .Select(row => (DateTime?)row.SnapshotDate)
                    .DefaultIfEmpty(null)
                    .Max()))
            // Deadest first: most days carried, least sold.
            .OrderByDescending(item => item.DaysOnVanWithoutSelling)
            .ThenBy(item => item.SellThroughRate ?? 0)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── C5: expiry ──────────────────────────────────────────────────────────────

    private static List<VanStockExpiryResult> BuildExpiring(
        List<SnapshotRow> snapshots,
        DateTime todayCat)
    {
        // The newest snapshot per van, because expiry is a question about what is on the van now
        // rather than about what was on it a fortnight ago.
        var newest = snapshots
            .GroupBy(snapshot => snapshot.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .Select(van => van.OrderByDescending(snapshot => snapshot.SnapshotDate).First())
            .ToList();

        return newest
            .SelectMany(snapshot => snapshot.Items
                .Where(item => item.ExpiryDate.HasValue
                               && item.OriginalQuantity > 0
                               && !string.IsNullOrWhiteSpace(item.BatchNumber))
                .Select(item => new VanStockExpiryResult(
                    VanWarehouseCode: snapshot.WarehouseCode,
                    ItemCode: item.ItemCode,
                    ItemDescription: item.ItemDescription,
                    BatchNumber: item.BatchNumber!,
                    ExpiryDate: item.ExpiryDate!.Value.Date,
                    DaysToExpiry: (int)(item.ExpiryDate.Value.Date - todayCat).TotalDays,
                    Quantity: item.OriginalQuantity,
                    SnapshotDate: snapshot.SnapshotDate)))
            .Where(expiry => expiry.DaysToExpiry <= ExpiryHorizonDays)
            // Already expired first, then soonest.
            .OrderBy(expiry => expiry.DaysToExpiry)
            .ThenByDescending(expiry => expiry.Quantity)
            .ToList();
    }

    // ── Summary and quality ─────────────────────────────────────────────────────

    private static VanStockSummaryResult BuildSummary(
        HashSet<string> vans,
        List<VanStockDayResult> days,
        List<VanStockItemResult> items,
        List<SnapshotRow> snapshots,
        DateTime? latest,
        DateTime? latestCountAt,
        DateTime todayCat,
        int deadStockDays,
        bool sapChecked) =>
        new(
            VanCount: vans.Count,
            SnapshotDayCount: days.Count,
            MissingSnapshotDays: CountMissingDays(snapshots),
            ItemCount: items.Count,
            // Carried on a van for the threshold and never sold once. Riding the round for a
            // fortnight is capital and shelf space that could be holding something that moves.
            DeadItemCount: items.Count(item =>
                item.DaysSold == 0 && item.DaysOnVanWithoutSelling >= deadStockDays),
            LoadedQuantity: days.Sum(day => day.LoadedQuantity),
            SoldQuantity: days.Sum(day => day.SoldQuantity),
            LatestSnapshotDate: latest,
            SnapshotAgeDays: latest is { } date ? Math.Max(0, (int)(todayCat - date).TotalDays) : null,
            SapChecked: sapChecked,
            LatestCountAt: latestCountAt);

    private static VanStockQualityResult BuildQuality(
        HashSet<string> vans,
        Dictionary<string, HashSet<string>> vanAccounts,
        List<SnapshotRow> snapshots,
        List<VanSaleLineFact> lines,
        DateTime? latest,
        DateTime todayCat,
        VanStockSapRead sap)
    {
        var withSnapshots = snapshots
            .Select(snapshot => snapshot.WarehouseCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new VanStockQualityResult(
            MissingSnapshotDays: CountMissingDays(snapshots),
            IncompleteSnapshots: snapshots.Count(snapshot => !snapshot.IsComplete),
            VansWithNoSnapshot: vans.Count(van => !withSnapshots.Contains(van)),
            SalesForWarehousesWithNoSnapshot: lines.Count(line => !withSnapshots.Contains(line.WarehouseCode!)),
            LatestSnapshotDate: latest,
            SnapshotAgeDays: latest is { } date ? Math.Max(0, (int)(todayCat - date).TotalDays) : null,
            SapProblem: sap.Available ? null : sap.Problem,
            VansWithNoAccount: vanAccounts.Count(pair => pair.Value.Count == 0));
    }

    /// <summary>
    /// Van-days between a van's first and last snapshot that have no snapshot of their own. Counted
    /// inside each van's own observed span, so a van that only came on the system halfway through the
    /// period is not charged for the days before it existed.
    /// </summary>
    private static int CountMissingDays(List<SnapshotRow> snapshots) =>
        snapshots
            .GroupBy(snapshot => snapshot.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .Sum(van =>
            {
                var dates = van.Select(snapshot => snapshot.SnapshotDate).Distinct().OrderBy(date => date).ToList();
                var span = (int)(dates[^1] - dates[0]).TotalDays + 1;
                return span - dates.Count;
            });

    private static Dictionary<string, decimal> SumByItem(SnapshotRow snapshot) =>
        snapshot.Items
            .GroupBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.OriginalQuantity),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A sale line in the unit stock is counted in. An online sale knows it; an offline one records
    /// only the unit it was sold in, which on this estate's vans is the inventory unit.
    /// </summary>
    private static decimal StockQuantity(VanSaleLineFact line) => line.InventoryQuantity ?? line.Quantity;

    private static string? DescriptionOf(
        SnapshotRow opening,
        SnapshotRow closing,
        List<VanStockSapMovement> moves,
        string itemCode) =>
        opening.Items.Concat(closing.Items)
            .Where(item => string.Equals(item.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.ItemDescription)
            .Concat(moves.Select(move => move.ItemDescription))
            .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description));

    private static string? Shorten(string? comments)
    {
        if (string.IsNullOrWhiteSpace(comments))
        {
            return null;
        }

        var flat = comments.ReplaceLineEndings(" ").Trim();
        return flat.Length <= CommentLength ? flat : flat[..(CommentLength - 1)] + "…";
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private readonly record struct StockKey(string WarehouseCode, DateTime SnapshotDate, string ItemCode);

    private sealed record SnapshotRow(
        string WarehouseCode,
        DateTime SnapshotDate,
        DateTime CountedAt,
        bool IsComplete,
        List<SnapshotItem> Items);

    private sealed record SnapshotItem(
        string ItemCode,
        string? ItemDescription,
        string? BatchNumber,
        decimal OriginalQuantity,
        DateTime? ExpiryDate);
}
