using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RateLimit.Queries.GetAllRateLimits;

public sealed record GetAllRateLimitsQuery(
    int Page,
    int PageSize,
    bool? BlockedOnly
) : IRequest<ErrorOr<RateLimitListResponseDto>>;
