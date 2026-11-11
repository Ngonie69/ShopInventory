using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services;

/// <summary>
/// The slots for <c>STK_ITEMS</c> stock reads, shared by both SAP clients in this process. Why there is
/// a limit: <see cref="ShopInventory.Middleware.SAPStockItemsReadHandler"/>.
/// </summary>
/// <remarks>
/// In memory and per process, like <see cref="SapStuckReadState"/>. The production API runs as one
/// process, so this is the whole of its <c>STK_ITEMS</c> traffic.
/// </remarks>
public sealed class SapStockItemsReadGate
{
    private readonly SemaphoreSlim? _slots;
    private readonly TimeProvider _timeProvider;

    public SapStockItemsReadGate(IOptions<SAPSettings> settings, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        Limit = Math.Max(0, settings.Value.StockItemsMaxConcurrentReads);
        InteractiveMaxWait = TimeSpan.FromSeconds(Math.Max(0, settings.Value.StockItemsInteractiveMaxWaitSeconds));
        _slots = Limit > 0 ? new SemaphoreSlim(Limit, Limit) : null;
    }

    /// <summary>How many reads may be in flight at once; 0 when the limit is off.</summary>
    public int Limit { get; }

    public bool Enabled => _slots is not null;

    /// <summary>How long a read a person is waiting on queues before it is sent without a slot.</summary>
    public TimeSpan InteractiveMaxWait { get; }

    /// <summary>Slots free right now.</summary>
    public int Available => _slots?.CurrentCount ?? 0;

    /// <summary>
    /// Takes a slot, waiting for one for at most <paramref name="maxWait"/>, or until
    /// <paramref name="cancellationToken"/> when it is null. True when a slot was taken, and must be
    /// given back with <see cref="Exit"/>; false when the wait ran out first.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled while waiting.</exception>
    public async Task<bool> EnterAsync(TimeSpan? maxWait, CancellationToken cancellationToken)
    {
        var slots = _slots ?? throw new InvalidOperationException("The STK_ITEMS read limit is turned off.");

        if (maxWait is null)
        {
            await slots.WaitAsync(cancellationToken);
            return true;
        }

        if (slots.Wait(0, CancellationToken.None))
        {
            return true;
        }

        // On the injected clock rather than SemaphoreSlim's own timeout, so the wait is the same
        // TimeProvider the rest of the SAP pipeline measures with.
        using var limit = new CancellationTokenSource(maxWait.Value, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            await slots.WaitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Gives back a slot taken by <see cref="EnterAsync"/>.</summary>
    public void Exit() => _slots?.Release();
}
