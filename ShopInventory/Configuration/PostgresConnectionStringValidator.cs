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

        var poolCapped = ApplyPoolCaps(builder, policy, connectionName);

        Log.Information(
            "Validated PostgreSQL connection {ConnectionName}: Hosts={Hosts}; MultiHost={MultiHost}; Database={Database}; Port={Port}",
            connectionName,
            string.Join(",", hosts),
            hosts.Length > 1,
            builder.Database,
            builder.Port);

        return poolCapped ? builder.ConnectionString : connectionString;
    }

    /// <summary>
    /// Lowers the pool bounds to the policy's caps. Only ever lowers: a string already inside them is
    /// returned untouched.
    /// </summary>
    /// <remarks>
    /// The production connection string lives in each slot's web.config and is carried forward on every
    /// deploy, so it kept 100 connections and 10 idle per process while the server allows 200 in all. One
    /// API process and the Web app could take every connection between them, before counting the
    /// previous slot left running after a blue/green cutover. The caps ship in appsettings.json, so a
    /// release can change them without editing each server's web.config.
    /// </remarks>
    private static bool ApplyPoolCaps(
        NpgsqlConnectionStringBuilder builder,
        PostgresConnectionPolicyOptions policy,
        string connectionName)
    {
        var originalMax = builder.MaxPoolSize;
        var originalMin = builder.MinPoolSize;

        if (policy.MaximumPoolSize is > 0 and var maximum && builder.MaxPoolSize > maximum)
        {
            builder.MaxPoolSize = maximum;
        }

        if (policy.MinimumPoolSize is >= 0 and var minimum && builder.MinPoolSize > minimum)
        {
            builder.MinPoolSize = minimum;
        }

        if (builder.MinPoolSize > builder.MaxPoolSize)
        {
            builder.MinPoolSize = builder.MaxPoolSize;
        }

        if (builder.MaxPoolSize == originalMax && builder.MinPoolSize == originalMin)
        {
            return false;
        }

        Log.Information(
            "Capped the PostgreSQL pool for {ConnectionName}: Maximum Pool Size {OriginalMax} -> {Max}, Minimum Pool Size {OriginalMin} -> {Min}",
            connectionName,
            originalMax,
            builder.MaxPoolSize,
            originalMin,
            builder.MinPoolSize);

        return true;
    }

    private static bool IsLocalHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, ".", StringComparison.OrdinalIgnoreCase);
    }
}