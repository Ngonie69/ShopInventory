using ErrorOr;
using MediatR;

namespace ShopInventory.Features.RateLimit.Commands.BlockClient;

public sealed record BlockClientCommand(
    string ClientId,
    int DurationMinutes,
    string? Reason
) : IRequest<ErrorOr<string>>;
