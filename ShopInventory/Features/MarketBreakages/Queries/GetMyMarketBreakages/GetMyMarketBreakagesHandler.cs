using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMyMarketBreakages;

public sealed class GetMyMarketBreakagesHandler(ApplicationDbContext context)
    : IRequestHandler<GetMyMarketBreakagesQuery, ErrorOr<List<MarketBreakageDetailDto>>>
{
    /// <summary>A handset list, not an archive.</summary>
    private const int MaxRows = 200;

    public async Task<ErrorOr<List<MarketBreakageDetailDto>>> Handle(
        GetMyMarketBreakagesQuery query,
        CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-query.Days);

        return await context.MarketBreakages
            .AsNoTracking()
            .Where(breakage => breakage.ReportedByUserId == query.UserId && breakage.CreatedAtUtc >= since)
            .OrderByDescending(breakage => breakage.CreatedAtUtc)
            .ThenByDescending(breakage => breakage.Id)
            .Take(MaxRows)
            .Select(MarketBreakageProjections.Detail)
            .ToListAsync(cancellationToken);
    }
}
