using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransfers;

public sealed class GetPendingTransfersHandler(
    ApplicationDbContext context,
    IPendingInventoryTransferEnricher enricher)
    : IRequestHandler<GetPendingTransfersQuery, ErrorOr<PendingInventoryTransferListResponseDto>>
{
    public async Task<ErrorOr<PendingInventoryTransferListResponseDto>> Handle(
        GetPendingTransfersQuery query,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var records = context.PendingInventoryTransfers.AsNoTracking().AsQueryable();

        // "all" lifts the default filter so callers can review decided transfers too.
        if (!string.Equals(query.Status, "all", StringComparison.OrdinalIgnoreCase))
        {
            var status = string.IsNullOrWhiteSpace(query.Status)
                ? PendingInventoryTransferStatuses.AwaitingApproval
                : query.Status.Trim();
            records = records.Where(item => item.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.WarehouseCode))
        {
            var warehouse = query.WarehouseCode.Trim();
            records = records.Where(item => item.FromWarehouse == warehouse || item.ToWarehouse == warehouse);
        }

        if (query.MineOnly)
            records = records.Where(item => item.CreatedByUserId == query.UserId);

        // Both bounds are inclusive days, so the upper one is compared as the start of the day
        // after. CreatedAtUtc is stored in UTC and the incoming dates carry no zone, so they are
        // read as UTC — the same reading every other dated list in this API takes.
        if (ToUtcDate(query.FromDate) is { } fromDate)
            records = records.Where(item => item.CreatedAtUtc >= fromDate);

        if (ToUtcDate(query.ToDate) is { } toDate)
        {
            var toExclusive = toDate.AddDays(1);
            records = records.Where(item => item.CreatedAtUtc < toExclusive);
        }

        var totalCount = await records.CountAsync(cancellationToken);
        var pageRecords = await records
            .OrderByDescending(item => item.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PendingInventoryTransferListResponseDto
        {
            Items = await enricher.EnrichAsync(pageRecords, query.UserId, includeLines: false, cancellationToken),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Npgsql rejects a DateTime whose Kind is not Utc against a timestamptz column, and a date
    /// parsed out of a query string has no Kind at all.
    /// </summary>
    private static DateTime? ToUtcDate(DateTime? value) => value is null
        ? null
        : DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
}
