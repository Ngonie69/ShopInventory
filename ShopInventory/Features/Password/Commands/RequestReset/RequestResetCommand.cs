using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Password.Commands.RequestReset;

public sealed record RequestResetCommand(
    string Email,
    string ClientIp
) : IRequest<ErrorOr<string>>;
