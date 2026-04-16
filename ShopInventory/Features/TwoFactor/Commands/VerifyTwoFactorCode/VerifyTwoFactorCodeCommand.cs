using ErrorOr;
using MediatR;

namespace ShopInventory.Features.TwoFactor.Commands.VerifyTwoFactorCode;

public sealed record VerifyTwoFactorCodeCommand(
    string Code,
    bool IsBackupCode,
    Guid UserId
) : IRequest<ErrorOr<string>>;
