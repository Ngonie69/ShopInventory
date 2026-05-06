using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface ISystemHealthService
{
    Task<SystemHealthApiResponse?> GetHealthAsync(CancellationToken cancellationToken = default);
}

public sealed class SystemHealthService(
    HttpClient httpClient,
    ILogger<SystemHealthService> logger
) : ISystemHealthService
{
    public async Task<SystemHealthApiResponse?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<SystemHealthApiResponse>("api/health", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch system health status from API");
            return new SystemHealthApiResponse
            {
                Status = "Unreachable",
                Timestamp = DateTime.UtcNow,
                Dependencies = new SystemHealthSection
                {
                    Status = "Unhealthy",
                    Checks =
                    [
                        new SystemHealthCheckEntry
                        {
                            Name = "api",
                            Status = "Unhealthy",
                            Description = "API is unreachable. Check network connectivity."
                        }
                    ]
                }
            };
        }
    }
}
