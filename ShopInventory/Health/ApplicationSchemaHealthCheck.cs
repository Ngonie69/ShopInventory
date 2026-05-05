using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopInventory.Data;

namespace ShopInventory.Health;

public sealed class ApplicationSchemaHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    private static readonly string[] RequiredTables =
    [
        "Users",
        "OfflineQueueItems",
        "SapConnectionLogs",
        "MobileOrderPostProcessingQueue",
        "BackgroundWorkerClusterStates"
    ];

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (dbContext.Database.HasPendingModelChanges())
        {
            return HealthCheckResult.Unhealthy(
                "Database schema is incompatible with the current EF model.",
                data: new Dictionary<string, object>
                {
                    ["pendingModelChanges"] = true
                });
        }

        var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
        var pendingMigrationList = pendingMigrations.Take(10).ToArray();
        if (pendingMigrationList.Length > 0)
        {
            return HealthCheckResult.Unhealthy(
                "Database has pending EF Core migrations.",
                data: new Dictionary<string, object>
                {
                    ["pendingMigrations"] = pendingMigrationList
                });
        }

        var missingTables = new List<string>();
        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;

        try
        {
            if (closeConnection)
            {
                await connection.OpenAsync(cancellationToken);
            }

            foreach (var table in RequiredTables)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT EXISTS (
    SELECT 1
    FROM information_schema.tables
    WHERE table_schema = 'public'
      AND lower(table_name) = lower(@tableName)
)";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "tableName";
                parameter.Value = table;
                command.Parameters.Add(parameter);

                var exists = (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? false;
                if (!exists)
                {
                    missingTables.Add(table);
                }
            }
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }

        return missingTables.Count == 0
            ? HealthCheckResult.Healthy("Required database tables are present.")
            : HealthCheckResult.Unhealthy(
                "Required database tables are missing.",
                data: new Dictionary<string, object>
                {
                    ["missingTables"] = missingTables
                });
    }
}