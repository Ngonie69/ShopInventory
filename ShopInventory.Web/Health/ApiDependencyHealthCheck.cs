using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ShopInventory.Web.Health;

public sealed class ApiDependencyHealthCheck(IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("ShopInventoryApiUser");

        try
        {
            using var response = await client.GetAsync("health/live", cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("API dependency is reachable.")
                : HealthCheckResult.Unhealthy($"API dependency returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("API dependency is unreachable.", exception);
        }
    }
}