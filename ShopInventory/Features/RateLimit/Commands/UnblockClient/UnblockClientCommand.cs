using ErrorOr;
using MediatR;

namespace ShopInventory.Features.RateLimit.Commands.UnblockClient;

public sealed record UnblockClientCommand(
    string ClientId
) : IRequest<ErrorOr<string>>;
