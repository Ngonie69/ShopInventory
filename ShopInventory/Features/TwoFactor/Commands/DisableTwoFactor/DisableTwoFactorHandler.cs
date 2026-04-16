using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.TwoFactor.Commands.DisableTwoFactor;

public sealed class DisableTwoFactorHandler(
    ITwoFactorService twoFactorService,
    ILogger<DisableTwoFactorHandler> logger
) : IRequestHandler<DisableTwoFactorCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        DisableTwoFactorCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await twoFactorService.DisableTwoFactorAsync(command.UserId, command.Password, command.Code);
            if (!result.IsSuccess)
            {
                return Errors.TwoFactor.DisableFailed(result.Message);
            }

            return result.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error disabling 2FA for user {UserId}", command.UserId);
            return Errors.TwoFactor.DisableFailed(ex.Message);
        }
    }
}
