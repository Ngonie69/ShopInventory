using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ShopInventory.Services;

public sealed class PostgresInventoryLockService : IInventoryLockService
{
    private readonly ConcurrentDictionary<string, AdvisoryLockHandle> _activeLocks = new();
    private readonly TimeSpan _defaultLockDuration = TimeSpan.FromSeconds(30);
    private readonly string _connectionString;
    private readonly ILogger<PostgresInventoryLockService> _logger;

    public PostgresInventoryLockService(IConfiguration configuration, ILogger<PostgresInventoryLockService> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection is not configured.");
        _logger = logger;
    }

    public async Task<InventoryLockResult> TryAcquireLockAsync(
        string itemCode,
        string warehouseCode,
        string? batchNumber = null,
        TimeSpan? lockDuration = null,
        CancellationToken cancellationToken = default)
    {
        var lockRequest = new InventoryLockRequest
        {
            ItemCode = itemCode,
            WarehouseCode = warehouseCode,
            BatchNumber = batchNumber
        };

        var lockKey = lockRequest.ToLockKey();
        var advisoryKey = ComputeAdvisoryKey(lockKey);
        var duration = lockDuration ?? _defaultLockDuration;
        var expiresAtUtc = DateTime.UtcNow.Add(duration);
        var lockToken = $"{lockKey}:{Guid.NewGuid():N}";
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
                var retryAfter = GetRetryAfter(lockKey);
                return InventoryLockResult.Failure(
                    $"Lock already held for {itemCode} in warehouse {warehouseCode}" +
                    (batchNumber != null ? $" batch {batchNumber}" : string.Empty),
                    retryAfter);
            }

            var handle = new AdvisoryLockHandle(lockKey, lockToken, advisoryKey, connection, expiresAtUtc);
            if (!_activeLocks.TryAdd(lockToken, handle))
            {
                await ReleaseAdvisoryLockAsync(handle, CancellationToken.None);
                return InventoryLockResult.Failure("Unable to persist advisory lock state.");
            }

            connection = null;

            ScheduleExpiry(handle);

            _logger.LogDebug(
                "PostgreSQL advisory lock acquired: {LockKey}, Token: {LockToken}, Expires: {ExpiresAtUtc}",
                lockKey,
                lockToken,
                expiresAtUtc);

            return InventoryLockResult.Success(lockToken, expiresAtUtc);
        }
        catch
        {
            throw;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    public async Task<MultiLockResult> TryAcquireMultipleLocksAsync(
        IEnumerable<InventoryLockRequest> lockRequests,
        TimeSpan? lockDuration = null,
        CancellationToken cancellationToken = default)
    {
        var requests = lockRequests
            .OrderBy(request => request.ToLockKey(), StringComparer.Ordinal)
            .ToList();

        var acquiredTokens = new List<string>();
        var failures = new List<LockFailureDetail>();

        foreach (var request in requests)
        {
            var result = await TryAcquireLockAsync(
                request.ItemCode,
                request.WarehouseCode,
                request.BatchNumber,
                lockDuration,
                cancellationToken);

            if (result.Acquired)
            {
                acquiredTokens.Add(result.LockToken!);
                continue;
            }

            failures.Add(new LockFailureDetail
            {
                ItemCode = request.ItemCode,
                WarehouseCode = request.WarehouseCode,
                BatchNumber = request.BatchNumber,
                Reason = result.FailureReason ?? "Unknown reason",
                RetryAfter = result.RetryAfter
            });

            foreach (var token in acquiredTokens)
            {
                await ReleaseLockAsync(token);
            }

            return new MultiLockResult
            {
                AllAcquired = false,
                FailedLocks = failures
            };
        }

        return new MultiLockResult
        {
            AllAcquired = true,
            LockTokens = acquiredTokens,
            CombinedLockToken = $"multi:{Guid.NewGuid():N}",
            EarliestExpiry = DateTime.UtcNow.Add(lockDuration ?? _defaultLockDuration)
        };
    }

    public async Task<bool> ReleaseLockAsync(string lockToken)
    {
        if (!_activeLocks.TryRemove(lockToken, out var handle))
        {
            _logger.LogDebug("PostgreSQL advisory lock not found for release: {LockToken}", lockToken);
            return false;
        }

        handle.CancelExpiry();
        return await ReleaseAdvisoryLockAsync(handle, CancellationToken.None);
    }

    public async Task ReleaseMultipleLocksAsync(IEnumerable<string> lockTokens)
    {
        foreach (var lockToken in lockTokens)
        {
            await ReleaseLockAsync(lockToken);
        }
    }

    public async Task<bool> IsLockValidAsync(string lockToken)
    {
        if (!_activeLocks.TryGetValue(lockToken, out var handle))
        {
            return false;
        }

        if (handle.ExpiresAtUtc <= DateTime.UtcNow)
        {
            await ReleaseLockAsync(lockToken);
            return false;
        }

        return handle.Connection.State == ConnectionState.Open;
    }

    public Task<bool> ExtendLockAsync(string lockToken, TimeSpan extension)
    {
        if (!_activeLocks.TryGetValue(lockToken, out var handle))
        {
            return Task.FromResult(false);
        }

        lock (handle.SyncRoot)
        {
            if (handle.ExpiresAtUtc <= DateTime.UtcNow)
            {
                return Task.FromResult(false);
            }

            handle.ExpiresAtUtc = handle.ExpiresAtUtc.Add(extension);
            ScheduleExpiry(handle);
        }

        _logger.LogDebug("PostgreSQL advisory lock extended: {LockToken}, NewExpiry: {ExpiresAtUtc}", lockToken, handle.ExpiresAtUtc);
        return Task.FromResult(true);
    }

    private void ScheduleExpiry(AdvisoryLockHandle handle)
    {
        var delay = handle.ExpiresAtUtc - DateTime.UtcNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        lock (handle.SyncRoot)
        {
            handle.CancelExpiry();
            handle.ExpiryCancellation = new CancellationTokenSource();
            _ = ExpireLockAsync(handle.LockToken, delay, handle.ExpiryCancellation.Token);
        }
    }

    private async Task ExpireLockAsync(string lockToken, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                await ReleaseLockAsync(lockToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to expire advisory lock {LockToken}", lockToken);
        }
    }

    private async Task<bool> ReleaseAdvisoryLockAsync(AdvisoryLockHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            if (handle.Connection.State != ConnectionState.Open)
            {
                return true;
            }

            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", handle.Connection);
            command.Parameters.AddWithValue("key", handle.AdvisoryKey);

            var released = (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? false;
            _logger.LogDebug("PostgreSQL advisory lock released: {LockKey}, Token: {LockToken}", handle.LockKey, handle.LockToken);
            return released;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to release advisory lock {LockToken}", handle.LockToken);
            return false;
        }
        finally
        {
            handle.CancelExpiry();
            await handle.Connection.DisposeAsync();
        }
    }

    private TimeSpan GetRetryAfter(string lockKey)
    {
        var retryAfter = _activeLocks.Values
            .Where(handle => string.Equals(handle.LockKey, lockKey, StringComparison.Ordinal))
            .Select(handle => handle.ExpiresAtUtc - DateTime.UtcNow)
            .Where(remaining => remaining > TimeSpan.Zero)
            .DefaultIfEmpty(TimeSpan.FromSeconds(1))
            .Min();

        return retryAfter;
    }

    private static long ComputeAdvisoryKey(string lockKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(lockKey));
        return BitConverter.ToInt64(hash, 0);
    }

    private sealed class AdvisoryLockHandle(
        string lockKey,
        string lockToken,
        long advisoryKey,
        NpgsqlConnection connection,
        DateTime expiresAtUtc)
    {
        public object SyncRoot { get; } = new();

        public string LockKey { get; } = lockKey;

        public string LockToken { get; } = lockToken;

        public long AdvisoryKey { get; } = advisoryKey;

        public NpgsqlConnection Connection { get; } = connection;

        public DateTime ExpiresAtUtc { get; set; } = expiresAtUtc;

        public CancellationTokenSource? ExpiryCancellation { get; set; }

        public void CancelExpiry()
        {
            ExpiryCancellation?.Cancel();
            ExpiryCancellation?.Dispose();
            ExpiryCancellation = null;
        }
    }
}