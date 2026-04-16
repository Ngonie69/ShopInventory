using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.RateLimit.Commands.UpdateRateLimitConfig;

public sealed class UpdateRateLimitConfigHandler(
    IRateLimitService rateLimitService
) : IRequestHandler<UpdateRateLimitConfigCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        UpdateRateLimitConfigCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await rateLimitService.UpdateConfigurationAsync(command.Config, cancellationToken);
            return "Rate limit configuration updated successfully";
        }
        catch (Exception ex)
        {
            return Errors.RateLimit.UpdateFailed(ex.Message);
        }
    }
}
