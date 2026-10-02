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
    public async Task<ErrorOr<MarketBreakageListResponseDto>> Handle(
        GetMarketBreakagesQuery query,
        CancellationToken cancellationToken)
    {
        var breakages = MarketBreakageFilters.Search(context.MarketBreakages.AsNoTracking(), query.Search);

        // Counted before the status filter, so every tab can show how many it holds.
        var statusCounts = await breakages
            .GroupBy(breakage => breakage.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Status, group => group.Count, cancellationToken);

        foreach (var status in MarketBreakageStatuses.All)
            statusCounts.TryAdd(status, 0);

        breakages = MarketBreakageFilters.Status(breakages, query.Status);

        var totalCount = await breakages.CountAsync(cancellationToken);
        var items = await MarketBreakageFilters.Newest(breakages)
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
