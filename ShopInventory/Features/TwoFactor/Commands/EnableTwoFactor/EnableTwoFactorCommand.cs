using ErrorOr;
using MediatR;

namespace ShopInventory.Features.TwoFactor.Commands.EnableTwoFactor;

public sealed record EnableTwoFactorCommand(
    string Code,
    Guid UserId
) : IRequest<ErrorOr<string>>;
