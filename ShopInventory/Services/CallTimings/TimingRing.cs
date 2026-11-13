namespace ShopInventory.Services.CallTimings;

/// <summary>
/// A fixed-capacity ring of finished calls: adding past capacity drops the oldest.
/// </summary>
/// <remarks>
/// One lock around a struct copy. At the API's traffic (tens of calls a second at peak) that is far below
/// anything a request would notice, and it keeps a reader from ever seeing half of a record.
/// </remarks>
public sealed class TimingRing<T> where T : struct
{
    private readonly T[] _items;
    private readonly object _sync = new();
    private int _next;
    private long _added;

    public TimingRing(int capacity)
    {
        _items = new T[Math.Max(1, capacity)];
    }

    public int Capacity => _items.Length;

    /// <summary>How many calls have ever been added, kept or not.</summary>
    public long Added
    {
        get { lock (_sync) { return _added; } }
    }

    public void Add(in T item)
    {
        lock (_sync)
        {
            _items[_next] = item;
            _next = (_next + 1) % _items.Length;
            _added++;
        }
    }

    /// <summary>The kept calls that match <paramref name="keep"/>, oldest first, and whether any were dropped.</summary>
    public (List<T> Items, bool Dropped) Snapshot(Func<T, bool> keep)
    {
        lock (_sync)
        {
            var count = (int)Math.Min(_added, _items.Length);
            var start = _added > _items.Length ? _next : 0;
            var items = new List<T>();
            for (var i = 0; i < count; i++)
            {
                var item = _items[(start + i) % _items.Length];
                if (keep(item))
                {
                    items.Add(item);
                }
            }

            return (items, _added > _items.Length);
        }
    }

    /// <summary>The oldest kept call, or null when the ring is empty.</summary>
    public T? Oldest()
    {
        lock (_sync)
        {
            if (_added == 0)
            {
                return null;
            }

            return _added > _items.Length ? _items[_next] : _items[0];
        }
    }
}
