using ErrorOr;
using MediatR;

namespace ShopInventory.Features.TwoFactor.Commands.DisableTwoFactor;

public sealed record DisableTwoFactorCommand(
    string Password,
    string Code,
    Guid UserId
) : IRequest<ErrorOr<string>>;
