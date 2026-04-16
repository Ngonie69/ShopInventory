using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Queries.GetBlockedClients;

public sealed class GetBlockedClientsHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<GetBlockedClientsQuery, ErrorOr<List<ApiRateLimitDto>>>
{
    public async Task<ErrorOr<List<ApiRateLimitDto>>> Handle(
        GetBlockedClientsQuery request,
        CancellationToken cancellationToken)
    {
        var blocked = await rateLimitService.GetBlockedClientsAsync(cancellationToken);
        return blocked;
    }
}
