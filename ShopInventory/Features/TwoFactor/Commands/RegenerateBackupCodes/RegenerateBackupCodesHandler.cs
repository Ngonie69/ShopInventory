using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.TwoFactor.Commands.RegenerateBackupCodes;

public sealed class RegenerateBackupCodesHandler(
    ITwoFactorService twoFactorService,
    ILogger<RegenerateBackupCodesHandler> logger
) : IRequestHandler<RegenerateBackupCodesCommand, ErrorOr<List<string>>>
{
    public async Task<ErrorOr<List<string>>> Handle(
        RegenerateBackupCodesCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var backupCodes = await twoFactorService.RegenerateBackupCodesAsync(command.UserId, command.Code);
            return backupCodes;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Backup code regeneration failed for user {UserId}", command.UserId);
            return Errors.TwoFactor.RegenerateFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error regenerating backup codes for user {UserId}", command.UserId);
            return Errors.TwoFactor.RegenerateFailed(ex.Message);
        }
    }
}
