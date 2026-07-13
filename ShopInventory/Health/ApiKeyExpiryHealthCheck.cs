using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Health;

/// <summary>
/// Surfaces service-to-service API keys that are expired or approaching expiry so the lapse is
/// caught before it silently breaks unattended callers (e.g. the recurring POD report email job,
/// which authenticates with the MainIntegration key and fails with HTTP 401 once it expires).
///
/// Only keys the auth handler actually honours are evaluated: active keys with a non-empty value
/// (see <see cref="Services.AuthService"/>). The dev/baseline appsettings ships empty key values —
/// production injects the real value via secrets — so this check stays quiet in environments where
/// no usable key is configured. The key material itself is never included in the reported data.
/// </summary>
public sealed class ApiKeyExpiryHealthCheck(IOptionsMonitor<SecuritySettings> securityOptions) : IHealthCheck
{
    /// <summary>
    /// Report Degraded once a key is within this many days of expiring. Mirrors the warning window
    /// AuthService.ValidateApiKey logs at, so the health check and the log warning fire together.
    /// </summary>
    private const int WarningDays = 14;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var utcNow = DateTime.UtcNow;
        var usableKeys = securityOptions.CurrentValue.ApiKeys
            .Where(key => key.IsActive && !string.IsNullOrWhiteSpace(key.Key))
            .ToList();

        if (usableKeys.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy("No active API keys configured to monitor."));
        }

        var issues = new List<string>();
        var summaries = new List<string>();
        var status = HealthStatus.Healthy;

        foreach (var key in usableKeys)
        {
            if (!key.ExpiresAt.HasValue)
            {
                // ValidateApiKey rejects keys without an explicit expiry (rotation is mandatory),
                // so an active key in this state cannot authenticate at all.
                status = HealthStatus.Unhealthy;
                issues.Add($"{key.Name} has no expiration configured; it will be rejected until an ExpiresAt is set.");
                summaries.Add($"{key.Name}|expiresAt=none");
                continue;
            }

            var expiresAt = key.ExpiresAt.Value;
            var daysUntilExpiry = (expiresAt - utcNow).TotalDays;
            summaries.Add($"{key.Name}|expiresAt={expiresAt:O}|daysUntilExpiry={daysUntilExpiry:N1}");

            if (expiresAt <= utcNow)
            {
                status = HealthStatus.Unhealthy;
                issues.Add($"{key.Name} expired {expiresAt:yyyy-MM-dd}; service calls using it now fail with HTTP 401.");
            }
            else if (daysUntilExpiry <= WarningDays)
            {
                if (status == HealthStatus.Healthy)
                {
                    status = HealthStatus.Degraded;
                }

                issues.Add($"{key.Name} expires in {daysUntilExpiry:N0} day(s) ({expiresAt:yyyy-MM-dd}); rotate it soon.");
            }
        }

        var data = new Dictionary<string, object>
        {
            ["apiKeys"] = summaries.ToArray()
        };

        if (issues.Count > 0)
        {
            data["issues"] = issues.ToArray();
        }

        return Task.FromResult(status switch
        {
            HealthStatus.Unhealthy => HealthCheckResult.Unhealthy("One or more service API keys are expired or misconfigured.", data: data),
            HealthStatus.Degraded => HealthCheckResult.Degraded("One or more service API keys are approaching expiry.", data: data),
            _ => HealthCheckResult.Healthy("Service API keys are valid.", data)
        });
    }
}
