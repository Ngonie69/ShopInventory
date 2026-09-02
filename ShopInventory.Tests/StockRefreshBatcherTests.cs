using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the batching and cancellation behind the per-line stock figures on the two transfer forms.
/// </summary>
/// <remarks>
/// Both forms used to read stock one line at a time, with CancellationToken.None. Each read becomes a
/// SAP SQLQueries round trip holding one of only six process-wide slots and has been measured at up
/// to 265 seconds, so a six-line transfer could take most of the pool on its own — and keep holding
/// it after the operator changed the source warehouse or left the page. The production API log for
/// 2 September 2026 shows the shape of it: 33 GetStockForItemsInWarehouseQuery calls, every one
/// carrying exactly one item code.
///
/// What these tests pin is therefore about call count and cancellation, not about figures: how many
/// calls a burst of lines produces, how many can be in flight at once, and whether superseded work
/// is actually abandoned rather than left to finish.
/// </remarks>
public sealed class StockRefreshBatcherTests
{
    // Short enough to keep the suite quick, long enough that a burst queued in a loop lands inside
    // one window on a loaded CI machine.
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(50);

    /// <summary>Records every call: what was asked for, and whether the token was cancelled.</summary>
    private sealed class RecordingStockCache : IWarehouseStockCacheService
    {
        private readonly Func<Task>? _gate;
        private readonly List<string[]> _calls = [];
        private readonly object _sync = new();

        public RecordingStockCache(Func<Task>? gate = null) => _gate = gate;

        /// <summary>The item codes of each call, in order.</summary>
        public IReadOnlyList<string[]> Calls
        {
            get { lock (_sync) { return _calls.ToList(); } }
        }

        /// <summary>How many calls were inside GetStockForItemsAsync at the same moment.</summary>
        public int PeakConcurrency { get; private set; }

        public int Concurrency { get; private set; }

        /// <summary>Set when a call was still running at the moment its token was cancelled.</summary>
        public bool ObservedCancellation { get; private set; }

        /// <summary>Thrown by the next call, to exercise the failure path.</summary>
        public Exception? ThrowOnCall { get; set; }

        public async Task<IReadOnlyDictionary<string, ProductDto>> GetStockForItemsAsync(
            string warehouseCode,
            IEnumerable<string> itemCodes,
            CancellationToken cancellationToken = default)
        {
            var codes = itemCodes.ToArray();

            lock (_sync)
            {
                _calls.Add(codes);
                Concurrency++;
                PeakConcurrency = Math.Max(PeakConcurrency, Concurrency);
            }

            try
            {
                if (_gate is not null)
                    await _gate();

                if (ThrowOnCall is not null)
                {
                    var toThrow = ThrowOnCall;
                    ThrowOnCall = null;
                    throw toThrow;
                }

                cancellationToken.ThrowIfCancellationRequested();

                return codes.ToDictionary(
                    code => code,
                    code => new ProductDto { ItemCode = code, QuantityInStock = 7m, UoM = "EA" },
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
            finally
            {
                lock (_sync) { Concurrency--; }
            }
        }

        // Nothing else on the interface is on this path.
        public Task<WarehouseProductsPagedResponse?> GetCachedStockAsync(
            string warehouseCode, int page = 1, int pageSize = 20, string? search = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WarehouseProductsResponse?> GetAllCachedStockAsync(string warehouseCode) =>
            throw new NotSupportedException();

        public Task<ProductDto?> FindByBarcodeAsync(
            string warehouseCode, string barcode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WarehouseStockSummary> GetStockSummaryAsync(
            string warehouseCode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SyncWarehouseStockAsync(string warehouseCode) => throw new NotSupportedException();

        public Task<ShopInventory.Web.Data.CacheSyncInfo?> GetSyncStatusAsync(string warehouseCode) =>
            throw new NotSupportedException();

        public event EventHandler<string>? SyncCompleted { add { } remove { } }
    }

    /// <summary>Waits for <paramref name="predicate"/> rather than sleeping a guessed interval.</summary>
    private static async Task WaitFor(Func<bool> predicate, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    // ── One call for the whole form, not one per line ───────────────────────────────────────

    [Fact]
    public async Task Six_lines_queued_together_make_one_call_carrying_six_codes()
    {
        var cache = new RecordingStockCache();
        var batches = new List<StockRefreshBatch>();

        using var batcher = new StockRefreshBatcher(
            cache, NullLogger.Instance,
            batch => { lock (batches) { batches.Add(batch); } return Task.CompletedTask; },
            Window);

        // What choosing a source warehouse does on a six-line form.
        foreach (var code in new[] { "A1", "B2", "C3", "D4", "E5", "F6" })
            batcher.Request("KEFSHOP", code);

        await WaitFor(() => batches.Count > 0, "the batch to be applied");

        var call = Assert.Single(cache.Calls);
        Assert.Equal(6, call.Length);
        Assert.Equal(
            ["A1", "B2", "C3", "D4", "E5", "F6"],
            call.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_same_item_queued_twice_is_read_once()
    {
        var cache = new RecordingStockCache();
        var applied = 0;

        using var batcher = new StockRefreshBatcher(
            cache, NullLogger.Instance,
            _ => { Interlocked.Increment(ref applied); return Task.CompletedTask; },
            Window);

        // Two lines for the same item, and the operator re-picking one of them.
        batcher.Request("KEFSHOP", "A1");
        batcher.Request("KEFSHOP", "a1");
        batcher.Request("KEFSHOP", "A1");

        await WaitFor(() => Volatile.Read(ref applied) > 0, "the batch to be applied");

        Assert.Equal(["A1"], Assert.Single(cache.Calls));
    }

    [Fact]
    public async Task Lines_picked_while_a_call_is_running_ride_the_next_call_rather_than_a_second_slot()
    {
        // The real hazard: the slow read is in flight while the operator keeps working. Each pick
        // used to open a SAP slot of its own; now they queue behind the one call.
        var release = new TaskCompletionSource();
        var cache = new RecordingStockCache(() => release.Task);
        var batches = new List<StockRefreshBatch>();

        using var batcher = new StockRefreshBatcher(
            cache, NullLogger.Instance,
            batch => { lock (batches) { batches.Add(batch); } return Task.CompletedTask; },
            Window);

        batcher.Request("KEFSHOP", "FIRST");
        await WaitFor(() => cache.Calls.Count == 1, "the first call to start");

        foreach (var code in new[] { "SECOND", "THIRD", "FOURTH" })
            batcher.Request("KEFSHOP", code);

        // Long enough for a debounce window to elapse: nothing may start while the first call runs.
        await Task.Delay(Window * 4);
        Assert.Single(cache.Calls);

        release.SetResult();

        await WaitFor(() => cache.Calls.Count == 2, "the queued lines to be read");
        Assert.Equal(1, cache.PeakConcurrency);
        Assert.Equal(
            ["FOURTH", "SECOND", "THIRD"],
            cache.Calls[1].OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public async Task More_than_a_hundred_codes_are_split_rather_than_dropped()
    {
        // The API handler takes the first 100 codes and drops the rest silently, so the batcher
        // must not hand it more than that.
        var cache = new RecordingStockCache();
        var applied = 0;

        using var batcher = new StockRefreshBatcher(
            cache, NullLogger.Instance,
            _ => { Interlocked.Increment(ref applied); return Task.CompletedTask; },
            Window);

        for (var i = 0; i < 130; i++)
            batcher.Request("KEFSHOP", $"ITEM{i:000}");

        await WaitFor(() => Volatile.Read(ref applied) == 2, "both batches to be applied");

        Assert.Equal(2, cache.Calls.Count);
        Assert.Equal(100, cache.Calls[0].Length);
        Assert.Equal(30, cache.Calls[1].Length);
        Assert.Equal(130, cache.Calls.SelectMany(c => c).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ── Superseded work is cancelled, not left to finish ────────────────────────────────────

    [Fact]
    public async Task Changing_the_source_warehouse_cancels_the_call_in_flight()
    {
        var release = new TaskCompletionSource();
        var cache = new RecordingStockCache(() => release.Task);
        var batches = new List<StockRefreshBatch>();

        using var batcher = new StockRefreshBatcher(
            cache, NullLogger.Instance,
            batch => { lock (batches) { batches.Add(batch); } return Task.CompletedTask; },
            Window);

        batcher.Request("KEFSHOP", "A1");
        await WaitFor(() => cache.Calls.Count == 1, "the first call to start");

        // The operator picks a different source. The read in flight is answering a question nobody
        // is asking any more, and it is holding a SAP slot to do it.
        batcher.Request("DEPOT2", "A1");
        release.SetResult();

        await WaitFor(() => cache.Calls.Count == 2, "the new warehouse to be read");
        await WaitFor(() => batches.Count > 0, "the new batch to be applied");

        Assert.True(cache.ObservedCancellation, "the superseded read should have been cancelled");
        var batch = Assert.Single(batches);
        Assert.Equal("DEPOT2", batch.WarehouseCode);
    }

    [Fact]
    public async Task A_cancelled_batch_is_never_applied()
    {
        var release = new TaskCompletionSource();
        var cache = new RecordingStockCache(() => release.Task);
        var applied = 0;

        using var batcher = new StockRefreshBatcher(
            cache, NullLogger.Instance,
            _ => { Interlocked.Increment(ref applied); return Task.CompletedTask; },
            Window);

        batcher.Request("KEFSHOP", "A1");
        await WaitFor(() => cache.Calls.Count == 1, "the call to start");

        batcher.Cancel();
        release.SetResult();

        await Task.Delay(Window * 6);
        Assert.Equal(0, Volatile.Read(ref applied));
    }

    [Fact]
    public async Task Disposing_the_form_cancels_the_call_in_flight()
    {
        // Leaving the page used to leave a read running to completion — minutes of a SAP slot spent
        // on a figure with nowhere to go.
        var release = new TaskCompletionSource();
        var cache = new RecordingStockCache(() => release.Task);
        var applied = 0;

        var batcher = new StockRefreshBatcher(
            cache, NullLogger.Instance,
            _ => { Interlocked.Increment(ref applied); return Task.CompletedTask; },
            Window);

        batcher.Request("KEFSHOP", "A1");
        await WaitFor(() => cache.Calls.Count == 1, "the call to start");

        batcher.Dispose();
        release.SetResult();

        await WaitFor(() => cache.ObservedCancellation, "the read to be cancelled");

        // And a queue on a disposed batcher does nothing at all.
        batcher.Request("KEFSHOP", "B2");
        await Task.Delay(Window * 4);

        Assert.Single(cache.Calls);
        Assert.Equal(0, Volatile.Read(ref applied));
    }

    // ── The form is told either way ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_read_still_reports_the_batch_so_the_lines_stop_spinning()
    {
        var cache = new RecordingStockCache { ThrowOnCall = new HttpRequestException("SAP is down") };
        var batches = new List<StockRefreshBatch>();

        using var batcher = new StockRefreshBatcher(
            cache, NullLogger.Instance,
            batch => { lock (batches) { batches.Add(batch); } return Task.CompletedTask; },
            Window);

        batcher.Request("KEFSHOP", "A1");
        batcher.Request("KEFSHOP", "B2");

        await WaitFor(() => batches.Count > 0, "the failed batch to be reported");

        var batch = Assert.Single(batches);
        Assert.Empty(batch.Stock);
        Assert.Equal(["A1", "B2"], batch.RequestedItemCodes.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_batch_reports_every_code_asked_for_including_ones_with_no_row()
    {
        // A line for an item the warehouse holds no stock of gets no row back. It still has to stop
        // spinning, so the form clears from RequestedItemCodes rather than from Stock.
        var cache = new RecordingStockCache();
        var batches = new List<StockRefreshBatch>();

        using var batcher = new StockRefreshBatcher(
            cache, NullLogger.Instance,
            batch => { lock (batches) { batches.Add(batch); } return Task.CompletedTask; },
            Window);

        batcher.Request("KEFSHOP", "A1");
        await WaitFor(() => batches.Count > 0, "the batch to be applied");

        var batch = Assert.Single(batches);
        Assert.Contains("a1", batch.RequestedItemCodes);
        Assert.Equal(7m, batch.Stock["A1"].QuantityInStock);
    }
}
