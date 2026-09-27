namespace ShopInventory.Common.Caching;

/// <summary>
/// One async lock per key, dropped again once nobody holds or waits on it.
/// </summary>
/// <remarks>
/// Replaces a <c>ConcurrentDictionary&lt;string, SemaphoreSlim&gt;</c> filled with <c>GetOrAdd</c>
/// and never emptied. Report cache keys carry date ranges and customers, so that map gained a
/// semaphore for every distinct report anyone had ever run, for the life of the process.
/// <para>
/// Each entry counts its holder and waiters under one gate, and the last to leave removes it. A
/// caller arriving after the removal creates a fresh entry, which is safe: nobody can still be inside
/// the old one.
/// </para>
/// </remarks>
public sealed class KeyedAsyncLock
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries;

    public KeyedAsyncLock(IEqualityComparer<string>? comparer = null) =>
        _entries = new Dictionary<string, Entry>(comparer ?? StringComparer.Ordinal);

    /// <summary>Keys currently held or waited on.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Waits for the key, then returns a handle that releases it on dispose. Cancelling the wait
    /// leaves no trace behind.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Leave(string key, Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            entry.Semaphore.Release();
            owner.Leave(key, entry);
        }
    }
}
