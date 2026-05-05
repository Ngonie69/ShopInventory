using Npgsql;
using Serilog;

namespace ShopInventory.Configuration;

public static class PostgresConnectionStringValidator
{
    public static string Validate(
        string? connectionString,
        IHostEnvironment environment,
        PostgresConnectionPolicyOptions policy,
        string connectionName)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("${", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Connection string '{connectionName}' is missing or still contains placeholder values. Configure ConnectionStrings:{connectionName} with the PostgreSQL HA endpoint or multi-host string.");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Connection string '{connectionName}' is not a valid PostgreSQL connection string.", ex);
        }

        var hosts = (builder.Host ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (hosts.Length == 0)
        {
            throw new InvalidOperationException(
                $"Connection string '{connectionName}' must specify at least one PostgreSQL host.");
        }

        if (environment.IsProduction() && policy.EnforceRemoteHostInProduction && hosts.Any(IsLocalHost))
        {
            throw new InvalidOperationException(
                $"Connection string '{connectionName}' points to localhost in Production. Move PostgreSQL to a dedicated HA endpoint or remote host before starting the application.");
        }

        if (hosts.Length > 1 && policy.RequireReadWriteTargetForMultiHost)
        {
            var hasTargetSessionAttributes = builder.TryGetValue("Target Session Attributes", out var targetSessionAttributes);
            if (!hasTargetSessionAttributes ||
                !string.Equals(targetSessionAttributes?.ToString(), "read-write", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Connection string '{connectionName}' uses multiple PostgreSQL hosts but does not require 'Target Session Attributes=read-write'. Add it so the application does not attach write traffic to a standby after failover.");
            }
        }

        Log.Information(
            "Validated PostgreSQL connection {ConnectionName}: Hosts={Hosts}; MultiHost={MultiHost}; Database={Database}; Port={Port}",
            connectionName,
            string.Join(",", hosts),
            hosts.Length > 1,
            builder.Database,
            builder.Port);

        return connectionString;
    }

    private static bool IsLocalHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, ".", StringComparison.OrdinalIgnoreCase);
    }
}