using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Commands.CleanupRateLimits;

public sealed class CleanupRateLimitsHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<CleanupRateLimitsCommand, ErrorOr<int>>
{
    public async Task<ErrorOr<int>> Handle(
        CleanupRateLimitsCommand command,
        CancellationToken cancellationToken)
    {
        var cleaned = await rateLimitService.CleanupExpiredAsync(cancellationToken);
        return cleaned;
    }
}
