using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Queries.GetRateLimitConfig;

public sealed class GetRateLimitConfigHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<GetRateLimitConfigQuery, ErrorOr<RateLimitConfigDto>>
{
    public Task<ErrorOr<RateLimitConfigDto>> Handle(
        GetRateLimitConfigQuery request,
        CancellationToken cancellationToken)
    {
        var config = rateLimitService.GetConfiguration();
        ErrorOr<RateLimitConfigDto> result = config;
        return Task.FromResult(result);
    }
}
