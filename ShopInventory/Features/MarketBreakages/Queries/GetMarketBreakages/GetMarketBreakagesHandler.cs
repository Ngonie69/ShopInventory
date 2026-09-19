using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakages;

public sealed class GetMarketBreakagesHandler(ApplicationDbContext context)
    : IRequestHandler<GetMarketBreakagesQuery, ErrorOr<MarketBreakageListResponseDto>>
{
    public const string OpenFilter = "open";

    public async Task<ErrorOr<MarketBreakageListResponseDto>> Handle(
        GetMarketBreakagesQuery query,
        CancellationToken cancellationToken)
    {
        var breakages = context.MarketBreakages.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim().ToLower()}%";
            breakages = breakages.Where(breakage =>
                EF.Functions.Like(breakage.ReportedByName.ToLower(), pattern)
                || EF.Functions.Like(breakage.VanWarehouseCode.ToLower(), pattern)
                || (breakage.CardCode != null && EF.Functions.Like(breakage.CardCode.ToLower(), pattern))
                || (breakage.CardName != null && EF.Functions.Like(breakage.CardName.ToLower(), pattern))
                || breakage.Lines.Any(line => EF.Functions.Like(line.ItemCode.ToLower(), pattern)));
        }

        // Counted before the status filter, so every tab can show how many it holds.
        var statusCounts = await breakages
            .GroupBy(breakage => breakage.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Status, group => group.Count, cancellationToken);

        foreach (var status in MarketBreakageStatuses.All)
            statusCounts.TryAdd(status, 0);

        if (string.Equals(query.Status, OpenFilter, StringComparison.OrdinalIgnoreCase))
        {
            breakages = breakages.Where(breakage =>
                breakage.Status == MarketBreakageStatuses.Pending
                || breakage.Status == MarketBreakageStatuses.TransferFailed
                || breakage.Status == MarketBreakageStatuses.Transferring);
        }
        else if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = MarketBreakageStatuses.All.First(value =>
                string.Equals(value, query.Status, StringComparison.OrdinalIgnoreCase));
            breakages = breakages.Where(breakage => breakage.Status == status);
        }

        var totalCount = await breakages.CountAsync(cancellationToken);
        var items = await breakages
            .OrderByDescending(breakage => breakage.CreatedAtUtc)
            .ThenByDescending(breakage => breakage.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(MarketBreakageProjections.Summary)
            .ToListAsync(cancellationToken);

        return new MarketBreakageListResponseDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize,
            StatusCounts = statusCounts
        };
    }
}
