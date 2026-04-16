using ErrorOr;
using MediatR;

namespace ShopInventory.Features.RateLimit.Queries.CheckRateLimit;

public sealed record CheckRateLimitResult(
    bool IsAllowed,
    int CurrentRequests,
    int MaxRequests,
    int WindowSizeSeconds,
    DateTime WindowResetAt,
    bool IsBlocked,
    DateTime? BlockedUntil
);

public sealed record CheckRateLimitQuery(
    string ClientId
) : IRequest<ErrorOr<CheckRateLimitResult>>;
