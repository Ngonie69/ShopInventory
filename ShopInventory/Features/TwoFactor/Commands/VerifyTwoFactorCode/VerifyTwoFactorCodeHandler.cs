using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.TwoFactor.Commands.VerifyTwoFactorCode;

public sealed class VerifyTwoFactorCodeHandler(
    ITwoFactorService twoFactorService,
    ILogger<VerifyTwoFactorCodeHandler> logger
) : IRequestHandler<VerifyTwoFactorCodeCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        VerifyTwoFactorCodeCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await twoFactorService.VerifyCodeAsync(command.UserId, command.Code, command.IsBackupCode);
            if (!result.IsSuccess)
            {
                return Errors.TwoFactor.VerificationFailed(result.Message);
            }

            return result.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error verifying 2FA code for user {UserId}", command.UserId);
            return Errors.TwoFactor.VerificationFailed(ex.Message);
        }
    }
}
