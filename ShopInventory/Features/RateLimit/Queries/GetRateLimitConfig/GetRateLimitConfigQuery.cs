using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RateLimit.Queries.GetRateLimitConfig;

public sealed record GetRateLimitConfigQuery() : IRequest<ErrorOr<RateLimitConfigDto>>;
