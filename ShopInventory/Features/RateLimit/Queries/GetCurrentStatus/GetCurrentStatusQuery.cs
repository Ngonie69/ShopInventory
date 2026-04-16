using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RateLimit.Queries.GetCurrentStatus;

public sealed record GetCurrentStatusQuery(
    string ClientId
) : IRequest<ErrorOr<RateLimitStatusDto>>;
