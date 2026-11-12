using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services;

/// <summary>
/// The slots for each <c>SQLQueries</c> read family in <see cref="SAPSettings.ReadFamilies"/>, shared by
/// both SAP clients in this process. Why there is a limit, and how it is used:
/// <see cref="ShopInventory.Middleware.SAPReadFamilyHandler"/>.
/// </summary>
/// <remarks>
/// In memory and per process, like <see cref="SapStuckReadState"/>. The production API runs as one
/// process, so this is the whole of its traffic in these families. A recycle forgets the slots kept for
/// stuck reads, which costs at most one more stuck read per slot.
/// </remarks>
public sealed class SapReadFamilyGate
{
    private const string ListOpen = "/SQLQueries('";
    private const string ListClose = "')/List";

    private readonly SapReadFamily[] _families;

    public SapReadFamilyGate(IOptions<SAPSettings> settings, TimeProvider timeProvider)
    {
        var value = settings.Value;

        // Longest name first, so a family whose name extends another's is matched before it.
        _families = (value.ReadFamilies ?? new Dictionary<string, SapReadFamilySettings>())
            .Where(family => !string.IsNullOrWhiteSpace(family.Key) && family.Value is { MaxConcurrentReads: > 0 })
            .Select(family => new SapReadFamily(
                family.Key.Trim().ToUpperInvariant(),
                family.Value.MaxConcurrentReads,
                family.Value.SendInteractiveWhenFull,
                timeProvider))
            .OrderByDescending(family => family.Name.Length)
            .ThenBy(family => family.Name, StringComparer.Ordinal)
            .ToArray();

        InteractiveMaxWait = TimeSpan.FromSeconds(Math.Max(0, value.ReadFamilyInteractiveMaxWaitSeconds));
        BackgroundMaxWait = TimeSpan.FromSeconds(Math.Max(0, value.ReadFamilyBackgroundMaxWaitSeconds));
    }

    /// <summary>False when no family is limited.</summary>
    public bool Enabled => _families.Length > 0;

    public IReadOnlyList<SapReadFamily> Families => _families;

    /// <summary>How long a read a person is waiting on queues for a slot.</summary>
    public TimeSpan InteractiveMaxWait { get; }

    /// <summary>How long background work queues for a slot before it is refused.</summary>
    public TimeSpan BackgroundMaxWait { get; }

    /// <summary>The limited family <paramref name="queryCode"/> belongs to, or null when it is not limited.</summary>
    public SapReadFamily? FamilyOf(string queryCode)
    {
        foreach (var family in _families)
        {
            if (family.Includes(queryCode))
            {
                return family;
            }
        }

        return null;
    }

    /// <summary>
    /// The query code of a <c>GET SQLQueries('&lt;code&gt;')/List</c>, the read the limit is for, or null
    /// for any other request (the query lookup, writes, documents).
    /// </summary>
    public static string? QueryCodeOf(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is not { IsAbsoluteUri: true } uri)
        {
            return null;
        }

        var path = uri.AbsolutePath;
        if (!path.EndsWith(ListClose, StringComparison.Ordinal))
        {
            return null;
        }

        var start = path.IndexOf(ListOpen, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += ListOpen.Length;
        var end = path.Length - ListClose.Length;
        return end > start ? path[start..end] : null;
    }
}

/// <summary>One limited read family's slots.</summary>
/// <remarks>
/// A slot is taken by a read for as long as this process waits on it, and kept past that while SAP is
/// presumed to be still working on the read (<see cref="KeepUntil"/>): the Service Layer process is
/// still busy, so the family still has a read running there.
/// </remarks>
public sealed class SapReadFamily
{
    private readonly SemaphoreSlim _slots;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<string, List<KeptSlot>> _kept = new(StringComparer.Ordinal);
    private int _keptCount;

    internal SapReadFamily(string name, int limit, bool sendInteractiveWhenFull, TimeProvider timeProvider)
    {
        Name = name;
        Limit = limit;
        SendInteractiveWhenFull = sendInteractiveWhenFull;
        _timeProvider = timeProvider;
        _slots = new SemaphoreSlim(limit, limit);
    }

    /// <summary>The family's name, which is also its query-code prefix.</summary>
    public string Name { get; }

    /// <summary>How many of its reads may be at SAP at once.</summary>
    public int Limit { get; }

    /// <summary>See <see cref="SapReadFamilySettings.SendInteractiveWhenFull"/>.</summary>
    public bool SendInteractiveWhenFull { get; }

    /// <summary>Slots free right now.</summary>
    public int Available => _slots.CurrentCount;

    /// <summary>Slots kept for reads SAP is presumed to be still working on.</summary>
    public int Kept
    {
        get { lock (_sync) { return _keptCount; } }
    }

    /// <summary>
    /// Whether every slot is kept for a stuck read. Nothing then frees for minutes, so a wait measured
    /// in seconds would only delay the answer.
    /// </summary>
    public bool AllKept => Kept >= Limit;

    /// <summary>When the first kept slot comes free, or null when none is kept.</summary>
    public DateTimeOffset? FirstKeptSlotFreesAt
    {
        get
        {
            lock (_sync)
            {
                return _kept.Count == 0 ? null : _kept.Values.SelectMany(slots => slots).Min(slot => slot.Until);
            }
        }
    }

    /// <summary>Whether <paramref name="queryCode"/> is this family's: the name itself, or the name and an underscore.</summary>
    public bool Includes(string queryCode) =>
        queryCode.StartsWith(Name, StringComparison.Ordinal)
        && (queryCode.Length == Name.Length || queryCode[Name.Length] == '_');

    /// <summary>
    /// Takes a slot, waiting for one for at most <paramref name="maxWait"/>. True when a slot was taken,
    /// which must be given back with <see cref="Exit"/> or <see cref="KeepUntil"/>; false when the wait
    /// ran out first.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled while waiting.</exception>
    public async Task<bool> EnterAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_slots.Wait(0, CancellationToken.None))
        {
            return true;
        }

        if (maxWait <= TimeSpan.Zero)
        {
            return false;
        }

        // On the injected clock rather than SemaphoreSlim's own timeout, so the wait is the same
        // TimeProvider the rest of the SAP pipeline measures with.
        using var limit = new CancellationTokenSource(maxWait, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            await _slots.WaitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Takes a slot if one is free, without waiting.</summary>
    public bool TryEnter() => _slots.Wait(0, CancellationToken.None);

    /// <summary>Gives back a slot taken by <see cref="EnterAsync"/> or <see cref="TryEnter"/>.</summary>
    public void Exit() => _slots.Release();

    /// <summary>
    /// Keeps a slot the caller took, for the read named by <paramref name="key"/>, until
    /// <paramref name="until"/> or until <see cref="Release"/> is called for that read, whichever is first.
    /// </summary>
    public void KeepUntil(string key, DateTimeOffset until)
    {
        var due = until - _timeProvider.GetUtcNow();
        if (due <= TimeSpan.Zero)
        {
            Exit();
            return;
        }

        var slot = new KeptSlot(key, until);
        lock (_sync)
        {
            if (!_kept.TryGetValue(key, out var slots))
            {
                _kept[key] = slots = [];
            }

            slots.Add(slot);
            _keptCount++;

            // Created under the lock, so a callback that fires at once still finds its timer set.
            slot.Timer = _timeProvider.CreateTimer(
                static state =>
                {
                    var (family, kept) = ((SapReadFamily, KeptSlot))state!;
                    family.GiveBack(kept);
                },
                (this, slot),
                due,
                Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Gives back every slot kept for the read named by <paramref name="key"/>: SAP answered it, so it is
    /// presumed no longer stuck. Returns how many were given back.
    /// </summary>
    public int Release(string key)
    {
        KeptSlot[] slots;
        lock (_sync)
        {
            if (!_kept.TryGetValue(key, out var kept))
            {
                return 0;
            }

            slots = [.. kept];
        }

        foreach (var slot in slots)
        {
            GiveBack(slot);
        }

        return slots.Length;
    }

    private void GiveBack(KeptSlot slot)
    {
        ITimer? timer;
        lock (_sync)
        {
            if (slot.GivenBack)
            {
                return;
            }

            slot.GivenBack = true;
            timer = slot.Timer;
            if (_kept.TryGetValue(slot.Key, out var slots) && slots.Remove(slot) && slots.Count == 0)
            {
                _kept.Remove(slot.Key);
            }

            _keptCount--;
        }

        timer?.Dispose();
        _slots.Release();
    }

    private sealed class KeptSlot(string key, DateTimeOffset until)
    {
        public string Key { get; } = key;
        public DateTimeOffset Until { get; } = until;
        public ITimer? Timer { get; set; }
        public bool GivenBack { get; set; }
    }
}
