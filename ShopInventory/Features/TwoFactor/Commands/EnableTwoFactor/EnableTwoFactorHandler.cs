using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.TwoFactor.Commands.EnableTwoFactor;

public sealed class EnableTwoFactorHandler(
    ITwoFactorService twoFactorService,
    ILogger<EnableTwoFactorHandler> logger
) : IRequestHandler<EnableTwoFactorCommand, ErrorOr<List<string>>>
{
    public async Task<ErrorOr<List<string>>> Handle(
        EnableTwoFactorCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var backupCodes = await twoFactorService.EnableTwoFactorAsync(command.UserId, command.Code);
            return backupCodes;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "2FA enable failed for user {UserId}", command.UserId);
            return Errors.TwoFactor.EnableFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error enabling 2FA for user {UserId}", command.UserId);
            return Errors.TwoFactor.EnableFailed(ex.Message);
        }
    }
}
