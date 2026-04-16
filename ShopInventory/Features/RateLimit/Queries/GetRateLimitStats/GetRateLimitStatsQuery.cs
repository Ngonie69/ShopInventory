using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RateLimit.Queries.GetRateLimitStats;

public sealed record GetRateLimitStatsQuery() : IRequest<ErrorOr<RateLimitStatsDto>>;
