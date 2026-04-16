using ErrorOr;
using MediatR;

namespace ShopInventory.Features.RateLimit.Commands.ResetClient;

public sealed record ResetClientCommand(
    string ClientId
) : IRequest<ErrorOr<string>>;
