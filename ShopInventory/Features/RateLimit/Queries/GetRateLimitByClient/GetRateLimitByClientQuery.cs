using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RateLimit.Queries.GetRateLimitByClient;

public sealed record GetRateLimitByClientQuery(
    string ClientId
) : IRequest<ErrorOr<ApiRateLimitDto>>;
