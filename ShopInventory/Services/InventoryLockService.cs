using System.Collections.Concurrent;

namespace ShopInventory.Services;

/// <summary>
/// Service for managing short-lived locks on inventory items to prevent race conditions
/// during batch allocation and invoice posting.
/// </summary>
public interface IInventoryLockService
{
    /// <summary>
    /// Attempts to acquire a lock for a specific item/warehouse/batch combination.
    /// </summary>
    /// <param name="itemCode">The item code</param>
    /// <param name="warehouseCode">The warehouse code</param>
    /// <param name="batchNumber">Optional batch number (null for warehouse-level lock)</param>
    /// <param name="lockDuration">How long to hold the lock (default 30 seconds)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Lock result with token if successful</returns>
    Task<InventoryLockResult> TryAcquireLockAsync(
        string itemCode,
        string warehouseCode,
        string? batchNumber = null,
        TimeSpan? lockDuration = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire locks for multiple items at once.
    /// If any lock fails, all acquired locks are released.
    /// </summary>
    /// <param name="lockRequests">The items to lock</param>
    /// <param name="lockDuration">How long to hold locks</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result with all lock tokens if successful</returns>
    Task<MultiLockResult> TryAcquireMultipleLocksAsync(
        IEnumerable<InventoryLockRequest> lockRequests,
        TimeSpan? lockDuration = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a previously acquired lock.
    /// </summary>
    /// <param name="lockToken">The lock token to release</param>
    /// <returns>True if released successfully</returns>
    Task<bool> ReleaseLockAsync(string lockToken);

    /// <summary>
    /// Releases multiple locks at once.
    /// </summary>
    /// <param name="lockTokens">The lock tokens to release</param>
    Task ReleaseMultipleLocksAsync(IEnumerable<string> lockTokens);

    /// <summary>
    /// Validates that a lock is still held and hasn't expired.
    /// </summary>
    /// <param name="lockToken">The lock token to validate</param>
    /// <returns>True if lock is still valid</returns>
    Task<bool> IsLockValidAsync(string lockToken);

    /// <summary>
    /// Extends the duration of an existing lock.
    /// </summary>
    /// <param name="lockToken">The lock token to extend</param>
    /// <param name="extension">How much time to add</param>
    /// <returns>True if extended successfully</returns>
    Task<bool> ExtendLockAsync(string lockToken, TimeSpan extension);
}

/// <summary>
/// Request for acquiring an inventory lock
/// </summary>
public class InventoryLockRequest
{
    public string ItemCode { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public string? BatchNumber { get; set; }

    /// <summary>
    /// Creates a unique key for this lock request
    /// </summary>
    public string ToLockKey()
    {
        if (!string.IsNullOrEmpty(BatchNumber))
        {
            return $"inv_lock:{ItemCode}:{WarehouseCode}:{BatchNumber}";
        }
        return $"inv_lock:{ItemCode}:{WarehouseCode}";
    }
}

/// <summary>
/// Result of a lock acquisition attempt
/// </summary>
public class InventoryLockResult
{
    /// <summary>
    /// Whether the lock was acquired successfully
    /// </summary>
    public bool Acquired { get; set; }

    /// <summary>
    /// Token to use when releasing the lock
    /// </summary>
    public string? LockToken { get; set; }

    /// <summary>
    /// When the lock will expire
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Reason why lock could not be acquired (if failed)
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// How long until the conflicting lock expires (if waiting might help)
    /// </summary>
    public TimeSpan? RetryAfter { get; set; }

    public static InventoryLockResult Success(string lockToken, DateTime expiresAt)
    {
        return new InventoryLockResult
        {
            Acquired = true,
            LockToken = lockToken,
            ExpiresAt = expiresAt
        };
    }

    public static InventoryLockResult Failure(string reason, TimeSpan? retryAfter = null)
    {
        return new InventoryLockResult
        {
            Acquired = false,
            FailureReason = reason,
            RetryAfter = retryAfter
        };
    }
}

/// <summary>
/// Result of acquiring multiple locks
/// </summary>
public class MultiLockResult
{
    /// <summary>
    /// Whether all locks were acquired successfully
    /// </summary>
    public bool AllAcquired { get; set; }

    /// <summary>
    /// Individual lock tokens (only populated if AllAcquired is true)
    /// </summary>
    public List<string> LockTokens { get; set; } = new();

    /// <summary>
    /// Combined lock token for easy release
    /// </summary>
    public string? CombinedLockToken { get; set; }

    /// <summary>
    /// When the earliest lock expires
    /// </summary>
    public DateTime? EarliestExpiry { get; set; }

    /// <summary>
    /// Which locks failed to be acquired
    /// </summary>
    public List<LockFailureDetail> FailedLocks { get; set; } = new();
}

/// <summary>
/// Details about a failed lock acquisition
/// </summary>
public class LockFailureDetail
{
    public string ItemCode { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public string? BatchNumber { get; set; }
    public string Reason { get; set; } = string.Empty;
    public TimeSpan? RetryAfter { get; set; }
}

/// <summary>
/// In-memory implementation of inventory lock service.
/// For production with multiple instances, replace with Redis-based implementation.
/// </summary>
public class InMemoryInventoryLockService : IInventoryLockService
{
    private readonly ConcurrentDictionary<string, LockEntry> _locks = new();
    private readonly ILogger<InMemoryInventoryLockService> _logger;
    private readonly TimeSpan _defaultLockDuration = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(1);
    private DateTime _lastCleanup = DateTime.UtcNow;

    public InMemoryInventoryLockService(ILogger<InMemoryInventoryLockService> logger)
    {
        _logger = logger;
    }

    public Task<InventoryLockResult> TryAcquireLockAsync(
        string itemCode,
        string warehouseCode,
        string? batchNumber = null,
        TimeSpan? lockDuration = null,
        CancellationToken cancellationToken = default)
    {
        // Clean up expired locks periodically
        CleanupExpiredLocks();

        var lockRequest = new InventoryLockRequest
        {
            ItemCode = itemCode,
            WarehouseCode = warehouseCode,
            BatchNumber = batchNumber
        };

        var lockKey = lockRequest.ToLockKey();
        var duration = lockDuration ?? _defaultLockDuration;
        var expiresAt = DateTime.UtcNow.Add(duration);
        var lockToken = $"{lockKey}:{Guid.NewGuid():N}";

        var newEntry = new LockEntry
        {
            LockToken = lockToken,
            ExpiresAt = expiresAt,
            AcquiredAt = DateTime.UtcNow
        };

        // Try to add the lock
        if (_locks.TryAdd(lockKey, newEntry))
        {
            _logger.LogDebug("Lock acquired: {LockKey}, Token: {Token}, Expires: {Expiry}",
                lockKey, lockToken, expiresAt);

            return Task.FromResult(InventoryLockResult.Success(lockToken, expiresAt));
        }

        // Lock exists - check if it's expired
        if (_locks.TryGetValue(lockKey, out var existingEntry))
        {
            if (existingEntry.ExpiresAt < DateTime.UtcNow)
            {
                // Expired lock - try to replace it
                if (_locks.TryUpdate(lockKey, newEntry, existingEntry))
                {
                    _logger.LogDebug("Replaced expired lock: {LockKey}, Token: {Token}",
                        lockKey, lockToken);

                    return Task.FromResult(InventoryLockResult.Success(lockToken, expiresAt));
                }
            }

            // Active lock held by someone else
            var retryAfter = existingEntry.ExpiresAt - DateTime.UtcNow;
            if (retryAfter < TimeSpan.Zero)
                retryAfter = TimeSpan.FromMilliseconds(100);

            _logger.LogDebug("Lock conflict: {LockKey}, Retry after: {RetryAfter}",
                lockKey, retryAfter);

            return Task.FromResult(InventoryLockResult.Failure(
                $"Lock already held for {itemCode} in warehouse {warehouseCode}" +
                (batchNumber != null ? $" batch {batchNumber}" : ""),
                retryAfter));
        }

        // Race condition - try again
        if (_locks.TryAdd(lockKey, newEntry))
        {
            return Task.FromResult(InventoryLockResult.Success(lockToken, expiresAt));
        }

        return Task.FromResult(InventoryLockResult.Failure(
            "Unable to acquire lock due to concurrent access. Please retry."));
    }

    public async Task<MultiLockResult> TryAcquireMultipleLocksAsync(
        IEnumerable<InventoryLockRequest> lockRequests,
        TimeSpan? lockDuration = null,
        CancellationToken cancellationToken = default)
    {
        var requests = lockRequests.ToList();
        var acquiredTokens = new List<string>();
        var failures = new List<LockFailureDetail>();

        // Sort by key to prevent deadlocks (consistent ordering)
        var sortedRequests = requests.OrderBy(r => r.ToLockKey()).ToList();

        foreach (var request in sortedRequests)
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
            }
            else
            {
                failures.Add(new LockFailureDetail
                {
                    ItemCode = request.ItemCode,
                    WarehouseCode = request.WarehouseCode,
                    BatchNumber = request.BatchNumber,
                    Reason = result.FailureReason ?? "Unknown reason",
                    RetryAfter = result.RetryAfter
                });

                // Release all acquired locks since we failed
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
        }

        // All locks acquired successfully
        var combinedToken = $"multi:{Guid.NewGuid():N}";
        var expiresAt = DateTime.UtcNow.Add(lockDuration ?? _defaultLockDuration);

        return new MultiLockResult
        {
            AllAcquired = true,
            LockTokens = acquiredTokens,
            CombinedLockToken = combinedToken,
            EarliestExpiry = expiresAt
        };
    }

    public Task<bool> ReleaseLockAsync(string lockToken)
    {
        // Find and remove the lock with this token
        foreach (var kvp in _locks)
        {
            if (kvp.Value.LockToken == lockToken)
            {
                if (_locks.TryRemove(kvp.Key, out _))
                {
                    _logger.LogDebug("Lock released: {LockKey}, Token: {Token}",
                        kvp.Key, lockToken);
                    return Task.FromResult(true);
                }
            }
        }

        _logger.LogDebug("Lock not found for release: {Token}", lockToken);
        return Task.FromResult(false);
    }

    public async Task ReleaseMultipleLocksAsync(IEnumerable<string> lockTokens)
    {
        foreach (var token in lockTokens)
        {
            await ReleaseLockAsync(token);
        }
    }

    public Task<bool> IsLockValidAsync(string lockToken)
    {
        foreach (var kvp in _locks)
        {
            if (kvp.Value.LockToken == lockToken)
            {
                var isValid = kvp.Value.ExpiresAt > DateTime.UtcNow;
                return Task.FromResult(isValid);
            }
        }

        return Task.FromResult(false);
    }

    public Task<bool> ExtendLockAsync(string lockToken, TimeSpan extension)
    {
        foreach (var kvp in _locks)
        {
            if (kvp.Value.LockToken == lockToken)
            {
                var currentEntry = kvp.Value;
                if (currentEntry.ExpiresAt < DateTime.UtcNow)
                {
                    // Lock already expired
                    return Task.FromResult(false);
                }

                var newEntry = new LockEntry
                {
                    LockToken = currentEntry.LockToken,
                    ExpiresAt = currentEntry.ExpiresAt.Add(extension),
                    AcquiredAt = currentEntry.AcquiredAt
                };

                if (_locks.TryUpdate(kvp.Key, newEntry, currentEntry))
                {
                    _logger.LogDebug("Lock extended: {LockKey}, New expiry: {Expiry}",
                        kvp.Key, newEntry.ExpiresAt);
                    return Task.FromResult(true);
                }
            }
        }

        return Task.FromResult(false);
    }

    private void CleanupExpiredLocks()
    {
        if (DateTime.UtcNow - _lastCleanup < _cleanupInterval)
            return;

        _lastCleanup = DateTime.UtcNow;
        var now = DateTime.UtcNow;
        var expiredKeys = _locks
            .Where(kvp => kvp.Value.ExpiresAt < now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _locks.TryRemove(key, out _);
        }

        if (expiredKeys.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} expired locks", expiredKeys.Count);
        }
    }

    private class LockEntry
    {
        public string LockToken { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public DateTime AcquiredAt { get; set; }
    }
}
