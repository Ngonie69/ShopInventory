using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Queries.GetRateLimitByClient;

public sealed class GetRateLimitByClientHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<GetRateLimitByClientQuery, ErrorOr<ApiRateLimitDto>>
{
    public async Task<ErrorOr<ApiRateLimitDto>> Handle(
        GetRateLimitByClientQuery request,
        CancellationToken cancellationToken)
    {
        var rateLimit = await rateLimitService.GetByClientIdAsync(request.ClientId, cancellationToken);
        if (rateLimit == null)
            return Errors.RateLimit.ClientNotFound(request.ClientId);

        return rateLimit;
    }
}
