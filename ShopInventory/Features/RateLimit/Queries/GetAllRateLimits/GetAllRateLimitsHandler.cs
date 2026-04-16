using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Queries.GetAllRateLimits;

public sealed class GetAllRateLimitsHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<GetAllRateLimitsQuery, ErrorOr<RateLimitListResponseDto>>
{
    public async Task<ErrorOr<RateLimitListResponseDto>> Handle(
        GetAllRateLimitsQuery request,
        CancellationToken cancellationToken)
    {
        var result = await rateLimitService.GetAllAsync(request.Page, request.PageSize, request.BlockedOnly, cancellationToken);
        return result;
    }
}
