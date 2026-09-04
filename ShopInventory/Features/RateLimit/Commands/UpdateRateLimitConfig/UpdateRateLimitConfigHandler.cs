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
        // Checked before anything is written, because these limits now reach the live limiter.
        // A permit limit of 0 or a zero-length window makes ASP.NET Core throw while building a
        // partition, which happens on the request path for every request - so an unchecked save
        // here is an outage that outlives the request that caused it.
        var errors = Validate(command.Config);
        if (errors.Count > 0)
        {
            return errors;
        }

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

    private static List<Error> Validate(DTOs.RateLimitConfigDto config)
    {
        var errors = new List<Error>();

        if (config.MaxRequests < RateLimitConfigStore.MinPermitLimit
            || config.MaxRequests > RateLimitConfigStore.MaxPermitLimit)
        {
            errors.Add(Errors.RateLimit.InvalidConfiguration(
                $"maxRequests must be between {RateLimitConfigStore.MinPermitLimit} and "
                + $"{RateLimitConfigStore.MaxPermitLimit}; {config.MaxRequests} would stop the API answering at all."));
        }

        if (config.WindowSizeSeconds < RateLimitConfigStore.MinWindowSeconds
            || config.WindowSizeSeconds > RateLimitConfigStore.MaxWindowSeconds)
        {
            errors.Add(Errors.RateLimit.InvalidConfiguration(
                $"windowSizeSeconds must be between {RateLimitConfigStore.MinWindowSeconds} and "
                + $"{RateLimitConfigStore.MaxWindowSeconds}."));
        }

        if (config.BlockDurationMinutes < RateLimitConfigStore.MinBlockDurationMinutes
            || config.BlockDurationMinutes > RateLimitConfigStore.MaxBlockDurationMinutes)
        {
            errors.Add(Errors.RateLimit.InvalidConfiguration(
                $"blockDurationMinutes must be between {RateLimitConfigStore.MinBlockDurationMinutes} and "
                + $"{RateLimitConfigStore.MaxBlockDurationMinutes}."));
        }

        // A whitelist entry that is not an address matches no caller, so it is a typo that reads as
        // a working exemption until somebody is throttled who should not have been.
        foreach (var entry in config.WhitelistedIPs.Where(entry => !string.IsNullOrWhiteSpace(entry)))
        {
            if (!System.Net.IPAddress.TryParse(entry.Trim(), out _))
            {
                errors.Add(Errors.RateLimit.InvalidConfiguration(
                    $"whitelistedIPs contains '{entry}', which is not an IP address."));
            }
        }

        return errors;
    }
}
