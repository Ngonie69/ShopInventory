using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Health;

public sealed class WebAppSchemaHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    private static readonly string[] RequiredTables =
    [
        "CachedProducts",
        "AuditLogs",
        "AppSettings",
        "CustomerPortalUsers"
    ];

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<WebAppDbContext>>();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

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