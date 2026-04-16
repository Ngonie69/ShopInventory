using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Password.Commands.CompleteReset;

public sealed record CompleteResetCommand(
    string Token,
    string NewPassword,
    string ConfirmPassword,
    string ClientIp
) : IRequest<ErrorOr<string>>;
