using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.InventoryTransfers;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanReplenishmentReport;

/// <summary>
/// Builds the van replenishment report: whether the depots are keeping the vans stocked.
/// </summary>
/// <remarks>
/// A van's warehouse is the transfer's <c>ToWarehouse</c>; the depot it loads from is
/// <c>FromWarehouse</c>. Vans are identified by their warehouse assignment on the user record rather
/// than by a naming convention: a warehouse is a van when a rep is assigned to it and a depot supplies
/// it, so a van coded differently — or a store coded <c>VAN…</c> — is classified on what it is.
/// </remarks>
public sealed class GetVanReplenishmentReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetVanReplenishmentReportQuery, ErrorOr<VanReplenishmentReportResult>>
{
    /// <summary>
    /// How long after an approval, or after a post attempt started, the request is taken to be posting
    /// rather than stranded. A large transfer can spend minutes in SAP; one untouched past this never
    /// had its post run.
    /// </summary>
    private static readonly TimeSpan PostingGrace = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> DepotRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ApplicationRoles.Admin,
        ApplicationRoles.Manager,
        ApplicationRoles.DepotController,
        ApplicationRoles.StockController,
        ApplicationRoles.WashBay
    };

    private static readonly string[] OpenStatuses =
    [
        PendingInventoryTransferStatuses.AwaitingApproval,
        PendingInventoryTransferStatuses.Approved,
        PendingInventoryTransferStatuses.PostFailed
    ];

    public async Task<ErrorOr<VanReplenishmentReportResult>> Handle(
        GetVanReplenishmentReportQuery query,
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

        var (everyVan, assignedVans, supplierOf) = await LoadVanWarehousesAsync(cancellationToken);

        // The filter's choices come from before any filter applies, so picking one van or one depot
        // never shrinks the list of the others to choose from.
        var availableVans = assignedVans.Order(StringComparer.OrdinalIgnoreCase).ToList();
        var availableDepots = supplierOf.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var depot = string.IsNullOrWhiteSpace(query.DepotWarehouseCode) ? null : query.DepotWarehouseCode.Trim();
        if (depot is not null)
        {
            // An idle van gets a row under the depot that supplies it; a request is the depot's when
            // it draws on the depot, whichever depot the van is normally loaded from.
            assignedVans.RemoveWhere(van =>
                !string.Equals(supplierOf.GetValueOrDefault(van), depot, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.VanWarehouseCode))
        {
            var wanted = query.VanWarehouseCode.Trim();
            everyVan = everyVan
                .Where(code => string.Equals(code, wanted, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            assignedVans.IntersectWith(everyVan);
        }

        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(from, to);

        var requests = await db.PendingInventoryTransfers
            .AsNoTracking()
            .Where(transfer => transfer.CreatedAtUtc >= windowStartUtc
                               && transfer.CreatedAtUtc < windowEndUtc
                               && everyVan.Contains(transfer.ToWarehouse)
                               && (depot == null || transfer.FromWarehouse == depot))
            .Select(RequestRow.Projection)
            .ToListAsync(cancellationToken);

        // Every open request, whatever the period: a request stuck since before the start date is
        // the one a period filter would hide.
        var open = await db.PendingInventoryTransfers
            .AsNoTracking()
            .Where(transfer => OpenStatuses.Contains(transfer.Status)
                               && everyVan.Contains(transfer.ToWarehouse)
                               && (depot == null || transfer.FromWarehouse == depot))
            .Select(RequestRow.Projection)
            .ToListAsync(cancellationToken);

        var lastPosted = await LoadLastPostedAsync(everyVan, cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var unfilled = BuildWorklist(open, nowUtc);

        // A van gets a row when a rep is assigned to it, or when it asked for something in the period
        // — a van whose rep has since been deactivated still has a record worth reading.
        var vanCodes = assignedVans
            .Concat(requests.Select(request => request.ToWarehouse))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var vans = vanCodes
            .Select(warehouse => BuildVan(
                warehouse, assignedVans.Contains(warehouse), requests, unfilled, lastPosted, windowStartUtc, nowUtc))
            .OrderByDescending(van => van.UnfilledNowCount)
            .ThenBy(van => van.FilledWithinDayRate ?? 2)
            .ThenByDescending(van => van.RequestCount)
            .ThenBy(van => van.VanWarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VanReplenishmentReportResult(
            FromDate: from,
            ToDate: to,
            GeneratedAt: AuditService.ToCAT(nowUtc),
            Summary: BuildSummary(vans, requests, unfilled),
            Waits: BuildWaits(requests),
            Vans: vans,
            Unfilled: unfilled,
            DepotShortages: BuildDepotShortages(unfilled),
            Quality: BuildQuality(requests, vans),
            AvailableVans: availableVans,
            AvailableDepots: availableDepots);
    }

    /// <summary>
    /// Every warehouse any user record makes a van, the ones an active rep is assigned to now, and
    /// the depot each is loaded from.
    /// </summary>
    /// <remarks>
    /// The two differ by the vans of deactivated reps. Their open requests still need somebody, so
    /// they stay in the worklist; but a van nobody drives no longer earns a row saying it asked for
    /// nothing.
    /// </remarks>
    private async Task<(HashSet<string> Every, HashSet<string> Assigned, Dictionary<string, string> SupplierOf)> LoadVanWarehousesAsync(
        CancellationToken cancellationToken)
    {
        // Materialised as entities so the codes can be read by the entity's own helper. They are a
        // JSON list in one column, and re-implementing that parse here is how the two would drift.
        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.SupplyingWarehouseCode != null && user.AssignedWarehouseCodes != null)
            .ToListAsync(cancellationToken);

        var every = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var supplierOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            foreach (var code in user.GetWarehouseCodes())
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                every.Add(code.Trim());
                if (user.IsActive)
                {
                    assigned.Add(code.Trim());
                    supplierOf[code.Trim()] = user.SupplyingWarehouseCode!.Trim();
                }
                else
                {
                    supplierOf.TryAdd(code.Trim(), user.SupplyingWarehouseCode!.Trim());
                }
            }
        }

        return (every, assigned, supplierOf);
    }

    /// <summary>Each van's most recent posted load, over all time.</summary>
    private async Task<Dictionary<string, DateTime>> LoadLastPostedAsync(
        HashSet<string> vans,
        CancellationToken cancellationToken)
    {
        var rows = await db.PendingInventoryTransfers
            .AsNoTracking()
            .Where(transfer => transfer.PostedAtUtc != null && vans.Contains(transfer.ToWarehouse))
            .GroupBy(transfer => transfer.ToWarehouse)
            .Select(group => new { Warehouse = group.Key, LastPosted = group.Max(transfer => transfer.PostedAtUtc) })
            .ToListAsync(cancellationToken);

        var lastPosted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.LastPosted is not { } posted)
            {
                continue;
            }

            if (!lastPosted.TryGetValue(row.Warehouse, out var existing) || posted > existing)
            {
                lastPosted[row.Warehouse] = posted;
            }
        }

        return lastPosted;
    }

    private static VanReplenishmentVanResult BuildVan(
        string warehouse,
        bool isAssigned,
        List<RequestRow> requests,
        List<VanReplenishmentOpenRequestResult> unfilled,
        Dictionary<string, DateTime> lastPostedByVan,
        DateTime windowStartUtc,
        DateTime nowUtc)
    {
        var mine = requests
            .Where(request => string.Equals(request.ToWarehouse, warehouse, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var toPosting = HoursToPosting(mine);
        DateTime? lastPosted = lastPostedByVan.TryGetValue(warehouse, out var posted) ? posted : null;

        return new VanReplenishmentVanResult(
            VanWarehouseCode: warehouse,
            DepotWarehouses: mine
                .Select(request => request.FromWarehouse)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IsAssigned: isAssigned,
            RequestCount: mine.Count,
            PostedCount: mine.Count(request => request.IsPosted),
            PartlyPostedCount: mine.Count(request => request.IsPosted && request.HasDroppedLines),
            RejectedCount: mine.Count(request => request.IsRejected),
            CancelledCount: mine.Count(request => request.IsCancelledByRequester),
            WithdrawnAfterFailureCount: mine.Count(request => request.IsWithdrawnAfterFailure),
            OpenCount: mine.Count(request => request.IsOpen),
            FilledWithinDayCount: mine.Count(IsFilledWithinDay),
            LineCount: mine.Sum(request => request.LineCount),
            TotalQuantity: mine.Sum(request => request.TotalQuantity),
            MedianHoursToDecision: Percentile(HoursToDecision(mine), 0.5),
            MedianHoursToPosting: Percentile(toPosting, 0.5),
            SlowestHoursToPosting: toPosting.Count == 0 ? null : Math.Round(toPosting.Max(), 1),
            LastRequestedAt: mine.Count == 0 ? null : AuditService.ToCAT(mine.Max(request => request.CreatedAtUtc)),
            LastPostedAt: lastPosted is { } last ? AuditService.ToCAT(last) : null,
            DaysSinceLastPosted: lastPosted is { } since ? Math.Max(0, (int)(nowUtc - since).TotalDays) : null,
            UnfilledNowCount: unfilled.Count(request =>
                string.Equals(request.VanWarehouseCode, warehouse, StringComparison.OrdinalIgnoreCase)))
        {
            LastPostedBeforePeriod = lastPosted is { } before && before < windowStartUtc
        };
    }

    private static VanReplenishmentSummaryResult BuildSummary(
        List<VanReplenishmentVanResult> vans,
        List<RequestRow> requests,
        List<VanReplenishmentOpenRequestResult> unfilled)
    {
        var toDecision = HoursToDecision(requests);
        var toPosting = HoursToPosting(requests);

        return new VanReplenishmentSummaryResult(
            VanCount: vans.Count,
            VansAsking: vans.Count(van => van.RequestCount > 0),
            RequestCount: requests.Count,
            PostedCount: requests.Count(request => request.IsPosted),
            PartlyPostedCount: requests.Count(request => request.IsPosted && request.HasDroppedLines),
            RejectedCount: requests.Count(request => request.IsRejected),
            CancelledCount: requests.Count(request => request.IsCancelledByRequester),
            WithdrawnAfterFailureCount: requests.Count(request => request.IsWithdrawnAfterFailure),
            OpenCount: requests.Count(request => request.IsOpen),
            LineCount: requests.Sum(request => request.LineCount),
            FilledWithinDayCount: requests.Count(IsFilledWithinDay),
            MedianHoursToDecision: Percentile(toDecision, 0.5),
            SlowestTenthHoursToDecision: Percentile(toDecision, 0.9),
            MedianHoursToPosting: Percentile(toPosting, 0.5),
            SlowestTenthHoursToPosting: Percentile(toPosting, 0.9),
            UnfilledNowCount: unfilled.Count,
            VansWaitingNow: unfilled
                .Select(request => request.VanWarehouseCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            OldestUnfilledDays: unfilled.Count == 0 ? null : unfilled.Max(request => request.DaysWaiting));
    }

    private static List<VanReplenishmentWaitBandResult> BuildWaits(List<RequestRow> requests)
    {
        var counts = requests
            .GroupBy(WaitBand)
            .ToDictionary(group => group.Key, group => group.Count());

        return VanReplenishmentWaitBands.All
            .Select(band => new VanReplenishmentWaitBandResult(band, counts.GetValueOrDefault(band)))
            .ToList();
    }

    private static string WaitBand(RequestRow request)
    {
        if (request.IsRejected) return VanReplenishmentWaitBands.TurnedDown;
        if (request.IsWithdrawnAfterFailure) return VanReplenishmentWaitBands.WithdrawnAfterFailure;
        if (request.IsCancelledByRequester) return VanReplenishmentWaitBands.CancelledByRequester;
        if (!request.IsPosted) return VanReplenishmentWaitBands.StillOpen;
        if (request.PostedAtUtc is not { } posted || posted < request.CreatedAtUtc)
            return VanReplenishmentWaitBands.PostedUntimed;

        return (posted - request.CreatedAtUtc).TotalHours switch
        {
            < 1 => VanReplenishmentWaitBands.UnderOneHour,
            < 4 => VanReplenishmentWaitBands.OneToFourHours,
            <= 24 => VanReplenishmentWaitBands.FourToTwentyFourHours,
            <= 72 => VanReplenishmentWaitBands.OneToThreeDays,
            _ => VanReplenishmentWaitBands.OverThreeDays
        };
    }

    private static List<VanReplenishmentOpenRequestResult> BuildWorklist(List<RequestRow> open, DateTime nowUtc) =>
        open
            .Select(request =>
            {
                var cause = Cause(request, nowUtc);
                var failure = cause == VanReplenishmentCauses.DepotShort
                    ? PendingTransferFailureClassifier.Classify(request.LastError)
                    : null;

                return new VanReplenishmentOpenRequestResult(
                    Id: request.Id,
                    DraftNumber: request.DraftNumber,
                    VanWarehouseCode: request.ToWarehouse,
                    DepotWarehouseCode: request.FromWarehouse,
                    Status: request.Status,
                    Cause: cause,
                    RequestedBy: request.CreatedByName,
                    RequestedByRole: request.CreatedByRole,
                    RaisedByDepot: request.CreatedByRole is { } role && DepotRoles.Contains(role),
                    RequestedAt: AuditService.ToCAT(request.CreatedAtUtc),
                    DecidedAt: request.DecidedAtUtc is { } decided ? AuditService.ToCAT(decided) : null,
                    LastAttemptedAt: request.LastAttemptedAtUtc is { } attempted ? AuditService.ToCAT(attempted) : null,
                    LineCount: request.LineCount,
                    TotalQuantity: request.TotalQuantity,
                    ShortLineCount: failure?.ShortLines.Count,
                    ShortLinesIncomplete: failure?.ShortLinesIncomplete ?? false,
                    ShortItems: failure is null
                        ? []
                        : failure.ShortLines
                            .GroupBy(line => line.ItemCode, StringComparer.OrdinalIgnoreCase)
                            .Select(group => new VanReplenishmentShortItemResult(group.Key, group.Sum(line => line.Shortage)))
                            .OrderByDescending(item => item.Shortage)
                            .ToList(),
                    LastError: request.LastError,
                    HoursWaiting: Math.Max(0, Math.Round((nowUtc - request.CreatedAtUtc).TotalHours, 1)));
            })
            .OrderBy(request => CauseOrder(request.Cause))
            .ThenByDescending(request => request.HoursWaiting)
            .ToList();

    private static string Cause(RequestRow request, DateTime nowUtc)
    {
        if (string.Equals(request.Status, PendingInventoryTransferStatuses.AwaitingApproval, StringComparison.OrdinalIgnoreCase))
        {
            return VanReplenishmentCauses.AwaitingDecision;
        }

        if (string.Equals(request.Status, PendingInventoryTransferStatuses.Approved, StringComparison.OrdinalIgnoreCase))
        {
            var lastTouched = request.LastAttemptedAtUtc ?? request.DecidedAtUtc ?? request.CreatedAtUtc;
            return nowUtc - lastTouched < PostingGrace
                ? VanReplenishmentCauses.Posting
                : VanReplenishmentCauses.ApprovedNeverPosted;
        }

        return PendingTransferFailureClassifier.Classify(request.LastError).Kind switch
        {
            PendingTransferFailureKinds.StockShort => VanReplenishmentCauses.DepotShort,
            PendingTransferFailureKinds.StockUnread => VanReplenishmentCauses.StockUnread,
            PendingTransferFailureKinds.OutcomeUnknown => VanReplenishmentCauses.OutcomeUnknown,
            _ => VanReplenishmentCauses.PostRefused
        };
    }

    /// <summary>
    /// The order the causes are read in: the ones that can move stock twice or never, first.
    /// </summary>
    private static int CauseOrder(string cause) => cause switch
    {
        VanReplenishmentCauses.OutcomeUnknown => 0,
        VanReplenishmentCauses.ApprovedNeverPosted => 1,
        VanReplenishmentCauses.DepotShort => 2,
        VanReplenishmentCauses.PostRefused => 3,
        VanReplenishmentCauses.StockUnread => 4,
        VanReplenishmentCauses.AwaitingDecision => 5,
        _ => 6
    };

    private static List<VanReplenishmentDepotShortageResult> BuildDepotShortages(
        List<VanReplenishmentOpenRequestResult> unfilled) =>
        unfilled
            .Where(request => request.Cause == VanReplenishmentCauses.DepotShort)
            .GroupBy(request => request.DepotWarehouseCode, StringComparer.OrdinalIgnoreCase)
            .Select(depot => new VanReplenishmentDepotShortageResult(
                DepotWarehouseCode: depot.Key,
                RequestCount: depot.Count(),
                VanCount: depot.Select(request => request.VanWarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                LineCount: depot.Sum(request => request.LineCount),
                LinesInStock: depot.Sum(request => request.LinesInStockAtLastAttempt ?? 0),
                Incomplete: depot.Any(request => request.ShortLinesIncomplete),
                Items: depot
                    .SelectMany(request => request.ShortItems.Select(item => (request.Id, item)))
                    .GroupBy(pair => pair.item.ItemCode, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new VanReplenishmentDepotShortItemResult(
                        item.Key,
                        item.Sum(pair => pair.item.Shortage),
                        item.Select(pair => pair.Id).Distinct().Count()))
                    .OrderByDescending(item => item.RequestCount)
                    .ThenByDescending(item => item.Shortage)
                    .ToList()))
            .OrderByDescending(depot => depot.RequestCount)
            .ToList();

    private static VanReplenishmentQualityResult BuildQuality(
        List<RequestRow> requests,
        List<VanReplenishmentVanResult> vans) =>
        new(
            RequestsWithoutDecisionTime: requests.Count(request =>
                (request.IsPosted || request.IsRejected || request.IsWithdrawnAfterFailure
                 || Is(request, PendingInventoryTransferStatuses.Approved)
                 || Is(request, PendingInventoryTransferStatuses.PostFailed))
                && !request.DecidedAtUtc.HasValue),
            RequestsWithoutPostTime: requests.Count(request => request.IsPosted && !request.PostedAtUtc.HasValue),
            PostedWithoutSapDocNum: requests.Count(request => request.IsPosted && !request.SapDocNum.HasValue),
            VansWithNoRequests: vans.Count(van => van.RequestCount == 0),
            PostsRecordedByHand: requests.Count(request => request.PostRecordedManually));

    /// <summary>
    /// Asking to SAP within a day. A partial post counts — the van got a load — and the page says how
    /// many were partial beside it.
    /// </summary>
    private static bool IsFilledWithinDay(RequestRow request) =>
        request.IsPosted
        && request.PostedAtUtc is { } posted
        && posted >= request.CreatedAtUtc
        && (posted - request.CreatedAtUtc).TotalHours <= 24;

    // Only requests that recorded the moment are measured. A missing timestamp is excluded and
    // counted, never treated as an instant decision — that would flatter the service level exactly
    // where the record is worst.
    private static List<double> HoursToDecision(IEnumerable<RequestRow> requests) =>
        requests
            .Where(request => request.DecidedAtUtc.HasValue && !request.IsCancelledByRequester)
            .Select(request => (request.DecidedAtUtc!.Value - request.CreatedAtUtc).TotalHours)
            .Where(hours => hours >= 0)
            .ToList();

    private static List<double> HoursToPosting(IEnumerable<RequestRow> requests) =>
        requests
            .Where(request => request.IsPosted && request.PostedAtUtc.HasValue)
            .Select(request => (request.PostedAtUtc!.Value - request.CreatedAtUtc).TotalHours)
            .Where(hours => hours >= 0)
            .ToList();

    private static bool Is(RequestRow request, string status) =>
        string.Equals(request.Status, status, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The middle wait (0.5), or the wait only one request in ten exceeded (0.9), by nearest rank —
    /// not the mean. One request left over a long weekend drags an average far enough to hide that
    /// everything else was decided the same morning.
    /// </summary>
    private static double? Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(value => value).ToList();

        if (percentile == 0.5)
        {
            var middle = sorted.Count / 2;
            var median = sorted.Count % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2;
            return Math.Round(median, 2);
        }

        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        return Math.Round(sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)], 2);
    }

    private sealed record RequestRow(
        Guid Id,
        string? DraftNumber,
        string ToWarehouse,
        string FromWarehouse,
        string Status,
        string CreatedByName,
        string? CreatedByRole,
        DateTime CreatedAtUtc,
        DateTime? DecidedAtUtc,
        DateTime? PostedAtUtc,
        DateTime? LastAttemptedAtUtc,
        DateTime? WithdrawnAtUtc,
        bool HasDroppedLines,
        bool PostRecordedManually,
        int LineCount,
        decimal TotalQuantity,
        int? SapDocNum,
        string? LastError)
    {
        public static readonly System.Linq.Expressions.Expression<Func<PendingInventoryTransferEntity, RequestRow>> Projection =
            transfer => new RequestRow(
                transfer.Id,
                transfer.DraftNumber,
                transfer.ToWarehouse,
                transfer.FromWarehouse,
                transfer.Status,
                transfer.CreatedByName,
                transfer.CreatedByRole,
                transfer.CreatedAtUtc,
                transfer.DecidedAtUtc,
                transfer.PostedAtUtc,
                transfer.LastAttemptedAtUtc,
                transfer.WithdrawnAtUtc,
                transfer.DroppedLinesJson != null,
                transfer.PostRecordedManually,
                transfer.LineCount,
                transfer.TotalQuantity,
                transfer.SapDocNum,
                transfer.LastError);

        public bool IsPosted => StatusIs(PendingInventoryTransferStatuses.Posted);

        public bool IsRejected => StatusIs(PendingInventoryTransferStatuses.Rejected);

        public bool IsWithdrawnAfterFailure => StatusIs(PendingInventoryTransferStatuses.Cancelled) && WithdrawnAtUtc.HasValue;

        public bool IsCancelledByRequester => StatusIs(PendingInventoryTransferStatuses.Cancelled) && !WithdrawnAtUtc.HasValue;

        public bool IsOpen =>
            StatusIs(PendingInventoryTransferStatuses.AwaitingApproval)
            || StatusIs(PendingInventoryTransferStatuses.Approved)
            || StatusIs(PendingInventoryTransferStatuses.PostFailed);

        private bool StatusIs(string status) => string.Equals(Status, status, StringComparison.OrdinalIgnoreCase);
    }
}
