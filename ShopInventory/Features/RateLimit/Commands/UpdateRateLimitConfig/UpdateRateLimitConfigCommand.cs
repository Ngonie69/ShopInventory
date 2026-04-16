using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RateLimit.Commands.UpdateRateLimitConfig;

public sealed record UpdateRateLimitConfigCommand(
    RateLimitConfigDto Config
) : IRequest<ErrorOr<string>>;
