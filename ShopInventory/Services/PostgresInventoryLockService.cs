using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ShopInventory.Services;

/// <summary>
/// Inventory locks held as PostgreSQL session advisory locks.
///
/// Every lock taken in one call shares one connection, which stays open until the last of
/// those locks is released or expires. An invoice asks for two locks per batch line, so a
/// connection per lock let a single 27-line invoice drain the API's 40-connection pool and
/// fail every other request until its locks expired.
/// </summary>
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
        var request = new InventoryLockRequest
        {
            ItemCode = itemCode,
            WarehouseCode = warehouseCode,
            BatchNumber = batchNumber
        };

        var outcome = await AcquireOnOneConnectionAsync([request], lockDuration ?? _defaultLockDuration, cancellationToken);
        if (outcome.Failure is { } failure)
        {
            return InventoryLockResult.Failure(failure.Reason, failure.RetryAfter);
        }

        var handle = outcome.Handles[0];
        return InventoryLockResult.Success(handle.LockToken, handle.ExpiresAtUtc);
    }

    public async Task<MultiLockResult> TryAcquireMultipleLocksAsync(
        IEnumerable<InventoryLockRequest> lockRequests,
        TimeSpan? lockDuration = null,
        CancellationToken cancellationToken = default)
    {
        var duration = lockDuration ?? _defaultLockDuration;
        var requests = lockRequests
            .OrderBy(request => request.ToLockKey(), StringComparer.Ordinal)
            .ToList();

        if (requests.Count == 0)
        {
            return new MultiLockResult
            {
                AllAcquired = true,
                CombinedLockToken = $"multi:{Guid.NewGuid():N}",
                EarliestExpiry = DateTime.UtcNow.Add(duration)
            };
        }

        var outcome = await AcquireOnOneConnectionAsync(requests, duration, cancellationToken);
        if (outcome.Failure is { } failure)
        {
            return new MultiLockResult
            {
                AllAcquired = false,
                FailedLocks = [failure]
            };
        }

        return new MultiLockResult
        {
            AllAcquired = true,
            LockTokens = outcome.Handles.Select(handle => handle.LockToken).ToList(),
            CombinedLockToken = $"multi:{Guid.NewGuid():N}",
            EarliestExpiry = outcome.Handles.Min(handle => handle.ExpiresAtUtc)
        };
    }

    public async Task<bool> ReleaseLockAsync(string lockToken)
    {
        if (!_activeLocks.TryRemove(lockToken, out var handle))
        {
            _logger.LogDebug("PostgreSQL advisory lock not found for release: {LockToken}", lockToken);
            return false;
        }

        lock (handle.SyncRoot)
        {
            handle.CancelExpiry();
        }

        return await ReleaseAdvisoryLockAsync(handle);
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

        return handle.Session.Connection.State == ConnectionState.Open;
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

    /// <summary>
    /// Takes every requested lock on one connection, or none of them. On a refused lock,
    /// a cancellation or any error, the locks already taken are released before returning.
    /// </summary>
    private async Task<AcquireOutcome> AcquireOnOneConnectionAsync(
        IReadOnlyList<InventoryLockRequest> requests,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var session = new LockSession(new NpgsqlConnection(_connectionString));
        var taken = new List<AdvisoryLockHandle>(requests.Count);
        var succeeded = false;

        try
        {
            await session.Connection.OpenAsync(cancellationToken);
            var expiresAtUtc = DateTime.UtcNow.Add(duration);

            foreach (var request in requests)
            {
                var lockKey = request.ToLockKey();
                var advisoryKey = ComputeAdvisoryKey(lockKey);

                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", session.Connection);
                command.Parameters.AddWithValue("key", advisoryKey);

                var acquired = (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? false;
                if (!acquired)
                {
                    return AcquireOutcome.Refused(new LockFailureDetail
                    {
                        ItemCode = request.ItemCode,
                        WarehouseCode = request.WarehouseCode,
                        BatchNumber = request.BatchNumber,
                        Reason = $"Lock already held for {request.ItemCode} in warehouse {request.WarehouseCode}" +
                            (request.BatchNumber != null ? $" batch {request.BatchNumber}" : string.Empty),
                        RetryAfter = GetRetryAfter(lockKey)
                    });
                }

                taken.Add(new AdvisoryLockHandle(lockKey, $"{lockKey}:{Guid.NewGuid():N}", advisoryKey, session, expiresAtUtc));
            }

            session.HeldCount = taken.Count;
            foreach (var handle in taken)
            {
                if (!_activeLocks.TryAdd(handle.LockToken, handle))
                {
                    // Tokens carry a fresh Guid, so this cannot collide in practice.
                    throw new InvalidOperationException($"Duplicate advisory lock token {handle.LockToken}.");
                }
            }

            foreach (var handle in taken)
            {
                lock (handle.SyncRoot)
                {
                    ScheduleExpiry(handle);
                }
            }

            succeeded = true;

            _logger.LogDebug(
                "PostgreSQL advisory locks acquired: {LockCount} on one connection, expiring {ExpiresAtUtc}",
                taken.Count,
                expiresAtUtc);

            return AcquireOutcome.Acquired(taken);
        }
        finally
        {
            if (!succeeded)
            {
                foreach (var handle in taken)
                {
                    _activeLocks.TryRemove(handle.LockToken, out _);
                }

                await AbandonSessionAsync(session, taken.Count);
            }
        }
    }

    /// <summary>
    /// Releases every lock a failed acquire took and gives the connection back. A pooled
    /// connection is only reset when it is next used, so the locks are dropped explicitly
    /// rather than left on an idle connection.
    /// </summary>
    private async Task AbandonSessionAsync(LockSession session, int takenCount)
    {
        try
        {
            if (takenCount > 0 && session.Connection.State == ConnectionState.Open)
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", session.Connection);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to release {LockCount} advisory lock(s) after a failed acquire", takenCount);
        }
        finally
        {
            await session.Connection.DisposeAsync();
            session.Gate.Dispose();
        }
    }

    private void ScheduleExpiry(AdvisoryLockHandle handle)
    {
        var delay = handle.ExpiresAtUtc - DateTime.UtcNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        handle.CancelExpiry();
        handle.ExpiryCancellation = new CancellationTokenSource();
        _ = ExpireLockAsync(handle.LockToken, delay, handle.ExpiryCancellation.Token);
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

    /// <summary>
    /// Unlocks one key on its shared connection. Commands on a connection cannot overlap, and
    /// all of a set's expiry timers fire together, so the session gate serialises them. The
    /// last lock out closes the connection.
    /// </summary>
    private async Task<bool> ReleaseAdvisoryLockAsync(AdvisoryLockHandle handle)
    {
        var session = handle.Session;
        await session.Gate.WaitAsync();
        try
        {
            if (session.Connection.State != ConnectionState.Open)
            {
                return true;
            }

            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", session.Connection);
            command.Parameters.AddWithValue("key", handle.AdvisoryKey);

            var released = (bool?)await command.ExecuteScalarAsync(CancellationToken.None) ?? false;
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
            session.HeldCount--;
            var lastLock = session.HeldCount <= 0;
            if (lastLock)
            {
                await session.Connection.DisposeAsync();
            }

            session.Gate.Release();
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

    private readonly record struct AcquireOutcome(IReadOnlyList<AdvisoryLockHandle> Handles, LockFailureDetail? Failure)
    {
        public static AcquireOutcome Acquired(IReadOnlyList<AdvisoryLockHandle> handles) => new(handles, null);

        public static AcquireOutcome Refused(LockFailureDetail failure) => new([], failure);
    }

    /// <summary>One open connection holding the advisory locks from one acquire call.</summary>
    private sealed class LockSession(NpgsqlConnection connection)
    {
        public NpgsqlConnection Connection { get; } = connection;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Locks still held on this connection. Changed only under <see cref="Gate"/> once published.</summary>
        public int HeldCount { get; set; }
    }

    private sealed class AdvisoryLockHandle(
        string lockKey,
        string lockToken,
        long advisoryKey,
        LockSession session,
        DateTime expiresAtUtc)
    {
        public object SyncRoot { get; } = new();

        public string LockKey { get; } = lockKey;

        public string LockToken { get; } = lockToken;

        public long AdvisoryKey { get; } = advisoryKey;

        public LockSession Session { get; } = session;

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
