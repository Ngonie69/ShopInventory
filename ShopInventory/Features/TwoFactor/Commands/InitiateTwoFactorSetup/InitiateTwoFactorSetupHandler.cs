using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.TwoFactor.Commands.InitiateTwoFactorSetup;

public sealed class InitiateTwoFactorSetupHandler(
    ITwoFactorService twoFactorService,
    ILogger<InitiateTwoFactorSetupHandler> logger
) : IRequestHandler<InitiateTwoFactorSetupCommand, ErrorOr<TwoFactorSetupResponse>>
{
    public async Task<ErrorOr<TwoFactorSetupResponse>> Handle(
        InitiateTwoFactorSetupCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var setupInfo = await twoFactorService.InitiateSetupAsync(command.UserId);
            return setupInfo;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "2FA setup failed for user {UserId}", command.UserId);
            return Errors.TwoFactor.SetupFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error initiating 2FA setup for user {UserId}", command.UserId);
            return Errors.TwoFactor.SetupFailed(ex.Message);
        }
    }
}
