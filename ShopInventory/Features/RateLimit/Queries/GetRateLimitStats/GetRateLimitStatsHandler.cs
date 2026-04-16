using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Queries.GetRateLimitStats;

public sealed class GetRateLimitStatsHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<GetRateLimitStatsQuery, ErrorOr<RateLimitStatsDto>>
{
    public async Task<ErrorOr<RateLimitStatsDto>> Handle(
        GetRateLimitStatsQuery request,
        CancellationToken cancellationToken)
    {
        var stats = await rateLimitService.GetStatsAsync(cancellationToken);
        return stats;
    }
}
