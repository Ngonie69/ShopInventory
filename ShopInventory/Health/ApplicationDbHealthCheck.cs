using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopInventory.Data;

namespace ShopInventory.Health;

public sealed class ApplicationDbHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
        return canConnect
            ? HealthCheckResult.Healthy("Application database is reachable.")
            : HealthCheckResult.Unhealthy("Application database is unreachable.");
    }
}