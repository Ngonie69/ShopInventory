using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalDayStates;

/// <summary>
/// Reads the fiscal day lifecycle table for the console.
/// </summary>
/// <remarks>
/// Read-only, and it asks the local table rather than the platform on purpose. The platform records each
/// step for itself, but an indeterminate close looks there exactly like one that never started, and the
/// taxpayer's deadline is measured from a time only the device knows. This table is what the scheduled
/// close resumes from, so it is also what the console has to show — the two must not disagree.
/// </remarks>
public sealed class GetFiscalDayStatesHandler(ApplicationDbContext db)
    : IRequestHandler<GetFiscalDayStatesQuery, ErrorOr<FiscalDayStateListResult>>
{
    /// <summary>The filter for the two statuses a person has to act on.</summary>
    public const string NeedsAttentionFilter = "needs-attention";

    public async Task<ErrorOr<FiscalDayStateListResult>> Handle(
        GetFiscalDayStatesQuery query,
        CancellationToken cancellationToken)
    {
        // Clamped, not just floored: the page number comes off a query string, and (page - 1) * pageSize
        // overflows to a negative Skip long before the list could ever be that long.
        var (page, pageSize) = FiscalConsolePaging.Clamp(query.Page, query.PageSize);

        var days = db.FiscalDayStates.AsNoTracking();

        if (query.DeviceId is > 0)
        {
            days = days.Where(day => day.DeviceId == query.DeviceId);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim();

            if (string.Equals(status, NeedsAttentionFilter, StringComparison.OrdinalIgnoreCase))
            {
                days = days.Where(day =>
                    day.Status == FiscalDayLifecycleStatus.NeedsReconciliation ||
                    day.Status == FiscalDayLifecycleStatus.Failed);
            }
            else if (Enum.TryParse<FiscalDayLifecycleStatus>(status, ignoreCase: true, out var parsed))
            {
                days = days.Where(day => day.Status == parsed);
            }
            else
            {
                // Refused rather than ignored. A filter that silently does nothing shows a full list
                // under a heading that claims it is narrowed, which is the shape of mistake this whole
                // page exists to stop.
                return Error.Validation(
                    "FiscalisationConsole.UnknownFiscalDayStatus",
                    $"'{query.Status}' is not a fiscal day status.");
            }
        }

        var totalCount = await days.CountAsync(cancellationToken);

        // Counted over the filtered set so the two figures always describe the list underneath them.
        var outstandingCount = await days
            .CountAsync(day => day.Status != FiscalDayLifecycleStatus.Submitted, cancellationToken);

        var needsAttentionCount = await days.CountAsync(
            day => day.Status == FiscalDayLifecycleStatus.NeedsReconciliation
                || day.Status == FiscalDayLifecycleStatus.Failed,
            cancellationToken);

        var rows = await days
            // Newest day first, and within a day the lowest device id, so a fleet's rows stay in a fixed
            // order between refreshes instead of shuffling with whichever device was touched last.
            .OrderByDescending(day => day.FiscalDayNo)
            .ThenBy(day => day.DeviceId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(day => new FiscalDayStateDto(
                day.Id,
                day.DeviceId,
                day.FiscalDayNo,
                day.Status.ToString(),
                day.OpenedAtLocal,
                day.MaxDurationHours,
                day.ClosedAtUtc,
                day.FileGeneratedAtUtc,
                day.FileSubmittedAtUtc,
                day.OfflineFileReference,
                day.IngestedReceiptCount,
                day.Attempts,
                day.LastError,
                day.DurationWarningRaised,
                day.Status == FiscalDayLifecycleStatus.Submitted,
                day.Status == FiscalDayLifecycleStatus.NeedsReconciliation
                    || day.Status == FiscalDayLifecycleStatus.Failed,
                day.UpdatedAt))
            .ToListAsync(cancellationToken);

        return new FiscalDayStateListResult(
            rows,
            totalCount,
            outstandingCount,
            needsAttentionCount,
            page,
            pageSize,
            page * pageSize < totalCount);
    }
}
