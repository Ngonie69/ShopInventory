using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.TwoFactor.Commands.EnableTwoFactor;

public sealed class EnableTwoFactorHandler(
    ITwoFactorService twoFactorService,
    ILogger<EnableTwoFactorHandler> logger
) : IRequestHandler<EnableTwoFactorCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        EnableTwoFactorCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await twoFactorService.EnableTwoFactorAsync(command.UserId, command.Code);
            if (!result.IsSuccess)
            {
                return Errors.TwoFactor.EnableFailed(result.Message);
            }

            return result.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error enabling 2FA for user {UserId}", command.UserId);
            return Errors.TwoFactor.EnableFailed(ex.Message);
        }
    }
}
