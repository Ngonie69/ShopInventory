using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// One batched stock read, handed back to the form that queued it.
/// </summary>
/// <param name="WarehouseCode">The source warehouse the batch was read for.</param>
/// <param name="RequestedItemCodes">
/// Every code in the batch, including ones the warehouse holds no row for. A form clears its
/// per-line spinners from this rather than from <paramref name="Stock"/>, which would leave a line
/// for an item with no stock spinning for ever.
/// </param>
/// <param name="Stock">The rows that came back, by item code. Empty when the read failed.</param>
public readonly record struct StockRefreshBatch(
    string WarehouseCode,
    IReadOnlySet<string> RequestedItemCodes,
    IReadOnlyDictionary<string, ProductDto> Stock);

/// <summary>
/// Collects a transfer form's per-line stock confirmations into one API call at a time.
/// </summary>
/// <remarks>
/// Each read becomes a SAP SQLQueries("STK_ITEMS_...")/List round trip holding one of only six
/// process-wide slots (SAP.MaxConcurrentRequests), and those reads have been measured at up to 265
/// seconds. A form that fired one per line could take most of the pool on its own — on 2 September
/// 2026 the API logged 33 of these, every one carrying exactly one item code.
///
/// Two things keep that from happening. Queued codes are coalesced over a short window, which folds
/// the burst a warehouse change produces into a single call; and only one call is ever in flight, so
/// codes queued while it runs ride the next one instead of opening a slot of their own. A form with
/// any number of lines therefore holds at most one slot.
///
/// Superseded work is cancelled rather than left to finish: changing the source warehouse or leaving
/// the page releases the slot instead of waiting minutes for an answer that will be discarded.
///
/// One of these belongs to one form (one circuit), created in OnInitialized and disposed with the
/// component. The apply callback runs off the circuit's synchronisation context, so it must go
/// through InvokeAsync before touching component state.
/// </remarks>
public sealed class StockRefreshBatcher : IDisposable
{
    /// <summary>
    /// The API's own ceiling: GetStockForItemsInWarehouseHandler takes the first 100 codes and drops
    /// the rest, so a bigger batch would lose lines silently. Any excess rides the next call.
    /// </summary>
    private const int MaxItemsPerCall = 100;

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(250);

    private readonly IWarehouseStockCacheService _stockCache;
    private readonly ILogger _logger;
    private readonly Func<StockRefreshBatch, Task> _applyAsync;
    private readonly TimeSpan _window;

    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingWarehouse;
    private CancellationTokenSource _cancellation = new();
    private bool _pumpRunning;
    private bool _disposed;

    public StockRefreshBatcher(
        IWarehouseStockCacheService stockCache,
        ILogger logger,
        Func<StockRefreshBatch, Task> applyAsync,
        TimeSpan? window = null)
    {
        _stockCache = stockCache;
        _logger = logger;
        _applyAsync = applyAsync;
        _window = window ?? DefaultWindow;
    }

    /// <summary>
    /// Queues one item's stock in one warehouse. Returns straight away; the figure arrives through
    /// the apply callback. Queueing a code that is already pending is free.
    /// </summary>
    public void Request(string? warehouseCode, string? itemCode)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode) || string.IsNullOrWhiteSpace(itemCode))
            return;

        CancellationTokenSource? superseded = null;

        lock (_gate)
        {
            if (_disposed)
                return;

            if (!string.Equals(_pendingWarehouse, warehouseCode, StringComparison.OrdinalIgnoreCase))
            {
                // The source moved. Nothing queued for the old warehouse is worth reading, and a
                // call already in flight for it is holding a SAP slot to answer a question nobody
                // is asking any more.
                superseded = ReplaceCancellation();
                _pending.Clear();
                _pendingWarehouse = warehouseCode.Trim();
            }

            _pending.Add(itemCode.Trim());

            if (!_pumpRunning)
            {
                _pumpRunning = true;
                _ = Task.Run(PumpAsync);
            }
        }

        // Outside the lock: Cancel runs its registrations synchronously — HttpClient's abort among
        // them — and doing that under the gate would block every other line trying to queue.
        superseded?.Cancel();
    }

    /// <summary>
    /// Drops what is queued and cancels the call in flight, for a form that has changed its source
    /// warehouse or been cleared. The batcher stays usable.
    /// </summary>
    public void Cancel()
    {
        CancellationTokenSource superseded;

        lock (_gate)
        {
            if (_disposed)
                return;

            superseded = ReplaceCancellation();
            _pending.Clear();
            _pendingWarehouse = null;
        }

        superseded.Cancel();
    }

    public void Dispose()
    {
        CancellationTokenSource superseded;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _pending.Clear();
            superseded = _cancellation;
        }

        superseded.Cancel();
    }

    /// <summary>
    /// Swaps in a fresh source and hands the old one back for the caller to cancel outside the lock.
    /// </summary>
    /// <remarks>
    /// The superseded source is deliberately not disposed. It carries no timer, so there is nothing
    /// unmanaged to release, and the pump may still be holding its token — disposing it underneath
    /// turns a clean cancellation into an ObjectDisposedException from the next registration.
    /// </remarks>
    private CancellationTokenSource ReplaceCancellation()
    {
        var superseded = _cancellation;
        _cancellation = new CancellationTokenSource();
        return superseded;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            CancellationToken token;

            lock (_gate)
            {
                if (_disposed || _pending.Count == 0)
                {
                    // Cleared under the same lock Request checks it under, so a code queued at this
                    // instant either starts a new pump or is picked up by this one.
                    _pumpRunning = false;
                    return;
                }

                token = _cancellation.Token;
            }

            try
            {
                // Coalesce the burst. Choosing a source warehouse queues every line at once, and an
                // operator working down the rows queues one per pick.
                await Task.Delay(_window, token);

                string warehouseCode;
                HashSet<string> codes;

                lock (_gate)
                {
                    if (_disposed || _pending.Count == 0 || _pendingWarehouse is null)
                        continue;

                    // The warehouse can only have changed by cancelling this token, so
                    // _pendingWarehouse still names the batch that was waited for.
                    warehouseCode = _pendingWarehouse;
                    codes = _pending.Take(MaxItemsPerCall).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    _pending.ExceptWith(codes);
                }

                IReadOnlyDictionary<string, ProductDto> stock;
                try
                {
                    stock = await _stockCache.GetStockForItemsAsync(warehouseCode, codes, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // The lines keep whatever the warehouse-wide read gave them, but the batch is
                    // still reported so their spinners stop.
                    _logger.LogWarning(ex, "Failed to read stock for {Count} item(s) in {Warehouse}",
                        codes.Count, warehouseCode);
                    stock = new Dictionary<string, ProductDto>(StringComparer.OrdinalIgnoreCase);
                }

                token.ThrowIfCancellationRequested();
                await _applyAsync(new StockRefreshBatch(warehouseCode, codes, stock));
            }
            catch (OperationCanceledException)
            {
                // Superseded by a new source warehouse, or the form is gone. Anything queued now
                // belongs to the new source, so loop round rather than stopping.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply a batched stock read");
            }
        }
    }
}
