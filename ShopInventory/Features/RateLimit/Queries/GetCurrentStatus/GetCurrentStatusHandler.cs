using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Queries.GetCurrentStatus;

public sealed class GetCurrentStatusHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<GetCurrentStatusQuery, ErrorOr<RateLimitStatusDto>>
{
    public async Task<ErrorOr<RateLimitStatusDto>> Handle(
        GetCurrentStatusQuery request,
        CancellationToken cancellationToken)
    {
        var status = await rateLimitService.GetRateLimitStatusAsync(request.ClientId, cancellationToken);
        return status;
    }
}
