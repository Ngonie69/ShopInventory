using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ShopInventory.Services;

public sealed class BackgroundWorkerLeaderElector
{
    private readonly string _connectionString;
    private readonly ILogger<BackgroundWorkerLeaderElector> _logger;

    public BackgroundWorkerLeaderElector(IConfiguration configuration, ILogger<BackgroundWorkerLeaderElector> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection is not configured.");
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string workerName, CancellationToken cancellationToken)
    {
        var advisoryKey = ComputeAdvisoryKey($"background-worker:{workerName}");
        NpgsqlConnection? connection = null;

        try
        {
            connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", advisoryKey);

            var acquired = (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? false;
            if (!acquired)
            {
                await connection.DisposeAsync();
                return null;
            }

            _logger.LogDebug("Background worker leadership acquired for {WorkerName}", workerName);
            return new LeadershipHandle(workerName, advisoryKey, connection, _logger);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            throw;
        }
    }

    private static long ComputeAdvisoryKey(string lockName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(lockName));
        return BitConverter.ToInt64(hash, 0);
    }

    private sealed class LeadershipHandle(
        string workerName,
        long advisoryKey,
        NpgsqlConnection connection,
        ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                    command.Parameters.AddWithValue("key", advisoryKey);
                    await command.ExecuteScalarAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release background worker leadership for {WorkerName}", workerName);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}