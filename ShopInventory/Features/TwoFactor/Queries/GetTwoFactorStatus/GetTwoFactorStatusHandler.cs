using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.TwoFactor.Queries.GetTwoFactorStatus;

public sealed class GetTwoFactorStatusHandler(
    ITwoFactorService twoFactorService,
    ILogger<GetTwoFactorStatusHandler> logger
) : IRequestHandler<GetTwoFactorStatusQuery, ErrorOr<TwoFactorStatusResponse>>
{
    public async Task<ErrorOr<TwoFactorStatusResponse>> Handle(
        GetTwoFactorStatusQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await twoFactorService.GetStatusAsync(query.UserId);
            return status;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "2FA status not found for user {UserId}", query.UserId);
            return Errors.TwoFactor.SetupFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting 2FA status for user {UserId}", query.UserId);
            return Errors.TwoFactor.SetupFailed(ex.Message);
        }
    }
}
