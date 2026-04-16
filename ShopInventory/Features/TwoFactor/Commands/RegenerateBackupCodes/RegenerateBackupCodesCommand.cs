using ErrorOr;
using MediatR;

namespace ShopInventory.Features.TwoFactor.Commands.RegenerateBackupCodes;

public sealed record RegenerateBackupCodesCommand(
    string Code,
    Guid UserId
) : IRequest<ErrorOr<List<string>>>;
