using ErrorOr;
using MediatR;

namespace ShopInventory.Features.RateLimit.Commands.CleanupRateLimits;

public sealed record CleanupRateLimitsCommand() : IRequest<ErrorOr<int>>;
