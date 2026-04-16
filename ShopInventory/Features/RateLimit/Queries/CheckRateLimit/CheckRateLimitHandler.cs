using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Queries.CheckRateLimit;

public sealed class CheckRateLimitHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<CheckRateLimitQuery, ErrorOr<CheckRateLimitResult>>
{
    public async Task<ErrorOr<CheckRateLimitResult>> Handle(
        CheckRateLimitQuery request,
        CancellationToken cancellationToken)
    {
        var isAllowed = await rateLimitService.IsRequestAllowedAsync(request.ClientId, cancellationToken);
        var status = await rateLimitService.GetRateLimitStatusAsync(request.ClientId, cancellationToken);

        return new CheckRateLimitResult(
            isAllowed,
            status.RequestsInWindow,
            status.MaxRequests,
            status.WindowSizeSeconds,
            status.WindowResetAt,
            status.IsBlocked,
            status.BlockedUntil);
    }
}
