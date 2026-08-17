using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanReplenishmentReport;

/// <summary>
/// Builds the van replenishment report: how well the depots are keeping the vans stocked.
/// </summary>
/// <remarks>
/// A van's warehouse is the transfer's <c>ToWarehouse</c>; the depot it loads from is
/// <c>FromWarehouse</c>. Vans are identified by their warehouse assignment on the user record rather
/// than by a naming convention — real warehouse codes here are alpha-coded and only the seed data
/// looks like VAN001, so matching on a prefix would work locally and find nothing in production.
/// </remarks>
public sealed class GetVanReplenishmentReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetVanReplenishmentReportQuery, ErrorOr<VanReplenishmentReportResult>>
{
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

        var vanWarehouses = await LoadVanWarehousesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.VanWarehouseCode))
        {
            var wanted = query.VanWarehouseCode.Trim();
            vanWarehouses = vanWarehouses
                .Where(code => string.Equals(code, wanted, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(from, to);

        var requests = await db.PendingInventoryTransfers
            .AsNoTracking()
            .Where(transfer => transfer.CreatedAtUtc >= windowStartUtc
                               && transfer.CreatedAtUtc < windowEndUtc
                               && vanWarehouses.Contains(transfer.ToWarehouse))
            .Select(transfer => new RequestRow(
                transfer.Id,
                transfer.ToWarehouse,
                transfer.FromWarehouse,
                transfer.Status,
                transfer.CreatedByName,
                transfer.CreatedAtUtc,
                transfer.DecidedAtUtc,
                transfer.PostedAtUtc,
                transfer.LineCount,
                transfer.TotalQuantity,
                transfer.SapDocNum,
                transfer.LastError))
            .ToListAsync(cancellationToken);

        var nowUtc = DateTime.UtcNow;

        var vans = vanWarehouses
            .Select(warehouse => BuildVan(warehouse, requests, nowUtc))
            .OrderByDescending(van => van.NeedingAttentionCount)
            .ThenByDescending(van => van.RequestCount)
            .ThenBy(van => van.VanWarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VanReplenishmentReportResult(
            FromDate: from,
            ToDate: to,
            Summary: BuildSummary(vans, requests),
            Vans: vans,
            NeedingAttention: BuildWorklist(requests, nowUtc),
            Quality: BuildQuality(requests, vans));
    }

    /// <summary>
    /// The warehouses that are vans, taken from the user records that assign them.
    /// </summary>
    /// <remarks>
    /// A van is a warehouse some rep is assigned to and which has a supplying depot behind it — that
    /// pairing is what makes it a van rather than a store. Reading it from the user table rather than
    /// from a code prefix matters: production warehouse codes are alpha-coded and named for places,
    /// and only the repository's seed data uses VAN-prefixed codes.
    /// </remarks>
    private async Task<HashSet<string>> LoadVanWarehousesAsync(CancellationToken cancellationToken)
    {
        // Materialised as entities so the codes can be read by the entity's own helper. They are a
        // JSON list in one column, and re-implementing that parse here is how the two would drift.
        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.SupplyingWarehouseCode != null && user.AssignedWarehouseCodes != null)
            .ToListAsync(cancellationToken);

        var warehouses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            foreach (var code in user.GetWarehouseCodes())
            {
                if (!string.IsNullOrWhiteSpace(code))
                {
                    warehouses.Add(code.Trim());
                }
            }
        }

        return warehouses;
    }

    private static VanReplenishmentVanResult BuildVan(
        string warehouse,
        List<RequestRow> requests,
        DateTime nowUtc)
    {
        var mine = requests
            .Where(request => string.Equals(request.ToWarehouse, warehouse, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Only requests that recorded the moment are measured. A missing timestamp is excluded and
        // counted, never treated as an instant decision — that would flatter the service level
        // exactly where the record is worst.
        var toDecision = mine
            .Where(request => request.DecidedAtUtc.HasValue)
            .Select(request => (request.DecidedAtUtc!.Value - request.CreatedAtUtc).TotalHours)
            .Where(hours => hours >= 0)
            .ToList();

        var toPosting = mine
            .Where(request => request.PostedAtUtc.HasValue)
            .Select(request => (request.PostedAtUtc!.Value - request.CreatedAtUtc).TotalHours)
            .Where(hours => hours >= 0)
            .ToList();

        var lastPosted = mine
            .Where(request => request.PostedAtUtc.HasValue)
            .Select(request => request.PostedAtUtc!.Value)
            .DefaultIfEmpty()
            .Max();

        return new VanReplenishmentVanResult(
            VanWarehouseCode: warehouse,
            DepotWarehouses: mine
                .Select(request => request.FromWarehouse)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            RequestCount: mine.Count,
            PostedCount: mine.Count(request => Is(request, PendingInventoryTransferStatuses.Posted)),
            RejectedCount: mine.Count(request => Is(request, PendingInventoryTransferStatuses.Rejected)),
            AwaitingApprovalCount: mine.Count(request =>
                Is(request, PendingInventoryTransferStatuses.AwaitingApproval)),
            PostFailedCount: mine.Count(request => Is(request, "PostFailed")),
            LineCount: mine.Sum(request => request.LineCount),
            TotalQuantity: mine.Sum(request => request.TotalQuantity),
            MedianHoursToDecision: Median(toDecision),
            MedianHoursToPosting: Median(toPosting),
            SlowestHoursToPosting: toPosting.Count == 0 ? null : Math.Round(toPosting.Max(), 1),
            LastRequestedAt: mine.Count == 0
                ? null
                : AuditService.ToCAT(mine.Max(request => request.CreatedAtUtc)),
            LastPostedAt: lastPosted == default ? null : AuditService.ToCAT(lastPosted))
        {
            DaysSinceLastPosted = lastPosted == default
                ? null
                : Math.Max(0, (int)(nowUtc - lastPosted).TotalDays)
        };
    }

    private static VanReplenishmentSummaryResult BuildSummary(
        List<VanReplenishmentVanResult> vans,
        List<RequestRow> requests)
    {
        var toDecision = requests
            .Where(request => request.DecidedAtUtc.HasValue)
            .Select(request => (request.DecidedAtUtc!.Value - request.CreatedAtUtc).TotalHours)
            .Where(hours => hours >= 0)
            .ToList();

        var toPosting = requests
            .Where(request => request.PostedAtUtc.HasValue)
            .Select(request => (request.PostedAtUtc!.Value - request.CreatedAtUtc).TotalHours)
            .Where(hours => hours >= 0)
            .ToList();

        return new VanReplenishmentSummaryResult(
            VanCount: vans.Count,
            RequestCount: requests.Count,
            PostedCount: requests.Count(request => Is(request, PendingInventoryTransferStatuses.Posted)),
            RejectedCount: requests.Count(request => Is(request, PendingInventoryTransferStatuses.Rejected)),
            AwaitingApprovalCount: requests.Count(request =>
                Is(request, PendingInventoryTransferStatuses.AwaitingApproval)),
            PostFailedCount: requests.Count(request => Is(request, "PostFailed")),
            LineCount: requests.Sum(request => request.LineCount),
            MedianHoursToDecision: Median(toDecision),
            MedianHoursToPosting: Median(toPosting));
    }

    private static List<VanReplenishmentRequestResult> BuildWorklist(
        List<RequestRow> requests,
        DateTime nowUtc) =>
        requests
            .Where(request => Is(request, PendingInventoryTransferStatuses.AwaitingApproval)
                              || Is(request, "PostFailed"))
            .Select(request => new VanReplenishmentRequestResult(
                Id: request.Id,
                VanWarehouseCode: request.ToWarehouse,
                DepotWarehouseCode: request.FromWarehouse,
                Status: request.Status,
                RequestedBy: request.CreatedByName,
                RequestedAt: AuditService.ToCAT(request.CreatedAtUtc),
                DecidedAt: request.DecidedAtUtc is { } decided ? AuditService.ToCAT(decided) : null,
                LineCount: request.LineCount,
                TotalQuantity: request.TotalQuantity,
                LastError: request.LastError,
                HoursWaiting: Math.Max(0, Math.Round((nowUtc - request.CreatedAtUtc).TotalHours, 1))))
            // A failed post first: it was decided and then lost, which nothing else surfaces.
            .OrderByDescending(request => request.IsPostFailure)
            .ThenByDescending(request => request.HoursWaiting)
            .ToList();

    private static VanReplenishmentQualityResult BuildQuality(
        List<RequestRow> requests,
        List<VanReplenishmentVanResult> vans) =>
        new(
            RequestsWithoutDecisionTime: requests.Count(request =>
                !Is(request, PendingInventoryTransferStatuses.AwaitingApproval)
                && !request.DecidedAtUtc.HasValue),
            RequestsWithoutPostTime: requests.Count(request =>
                Is(request, PendingInventoryTransferStatuses.Posted) && !request.PostedAtUtc.HasValue),
            PostedWithoutSapDocNum: requests.Count(request =>
                Is(request, PendingInventoryTransferStatuses.Posted) && !request.SapDocNum.HasValue),
            VansWithNoRequests: vans.Count(van => van.RequestCount == 0));

    private static bool Is(RequestRow request, string status) =>
        string.Equals(request.Status, status, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The middle wait, not the mean. One request left over a long weekend drags an average far
    /// enough to hide that everything else was decided the same morning.
    /// </summary>
    private static double? Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(value => value).ToList();
        var middle = sorted.Count / 2;

        var median = sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;

        return Math.Round(median, 1);
    }

    private sealed record RequestRow(
        Guid Id,
        string ToWarehouse,
        string FromWarehouse,
        string Status,
        string CreatedByName,
        DateTime CreatedAtUtc,
        DateTime? DecidedAtUtc,
        DateTime? PostedAtUtc,
        int LineCount,
        decimal TotalQuantity,
        int? SapDocNum,
        string? LastError);
}
