using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ShopInventory.Health;

public sealed class DatabaseConnectionLatencyHealthCheck(IConfiguration configuration) : IHealthCheck
{
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CriticalThreshold = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("DefaultConnection is not configured.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);

            stopwatch.Stop();
            var data = new Dictionary<string, object>
            {
                ["latencyMs"] = stopwatch.Elapsed.TotalMilliseconds,
                ["host"] = builder.Host ?? string.Empty,
                ["database"] = builder.Database ?? string.Empty,
                ["maxPoolSize"] = builder.MaxPoolSize
            };

            if (stopwatch.Elapsed >= CriticalThreshold)
            {
                return HealthCheckResult.Unhealthy("Database connection acquisition is critically slow.", data: data);
            }

            if (stopwatch.Elapsed >= WarningThreshold)
            {
                return HealthCheckResult.Degraded("Database connection acquisition is slow.", data: data);
            }

            return HealthCheckResult.Healthy("Database connection acquisition is healthy.", data);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return HealthCheckResult.Unhealthy(
                "Failed to acquire a database connection.",
                ex,
                new Dictionary<string, object>
                {
                    ["latencyMs"] = stopwatch.Elapsed.TotalMilliseconds,
                    ["host"] = builder.Host ?? string.Empty,
                    ["database"] = builder.Database ?? string.Empty,
                    ["maxPoolSize"] = builder.MaxPoolSize
                });
        }
    }
}