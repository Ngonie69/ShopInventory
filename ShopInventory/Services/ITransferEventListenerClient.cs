using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <summary>
/// Reads TransferEventListener, the service that watches SAP for stock transfers and posts them to
/// this API's <c>webhook/transfer-event</c> route.
/// </summary>
/// <remarks>
/// Infrastructure, in the sense the project instructions allow <c>Services/</c> to keep: an external
/// integration client with no business rules of its own. Every decision made on what it returns —
/// whether a stale poll is an alert, whether a degraded stock reading is good enough to put on a
/// till — belongs to the caller, and lives in a health check or a feature slice.
///
/// Transport failures surface as exceptions rather than nulls, because every caller here is already
/// inside a try/catch that has something specific to say about the failure. A null return means the
/// listener answered and had nothing, which is a different fact and is documented per method.
/// </remarks>
public interface ITransferEventListenerClient
{
    /// <summary>Whether this API is configured to call the listener at all.</summary>
    bool IsEnabled { get; }

    /// <summary>The listener's base URL, for display and for saying which host failed.</summary>
    string BaseUrl { get; }

    /// <summary>
    /// Asks the listener how its SAP poll loop is doing.
    /// </summary>
    /// <remarks>
    /// A 503 is a valid answer here, not a failure: it is how the listener reports itself unhealthy,
    /// and the body carries the reason. Only an unreachable host or an unreadable reply throws.
    /// </remarks>
    Task<TransferListenerHealthDto> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-batch stock for one warehouse, read through the listener's own SAP session.
    /// </summary>
    /// <returns>
    /// The reading, or <see langword="null"/> when the listener could not reach SAP and said so with
    /// a 503. Null is never an empty warehouse — the listener refuses to express that as an empty
    /// list precisely so a caller cannot mistake one for the other.
    /// </returns>
    Task<TransferListenerWarehouseStockDto?> GetWarehouseNonBatchStockAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The listener's counters since its process started. Not durable: a restart zeroes them.
    /// </summary>
    Task<TransferListenerStatsDto> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The warehouses the listener raises transfer events for.
    /// </summary>
    /// <remarks>
    /// Worth comparing against <c>DailyStockSettings.MonitoredWarehouses</c>: a warehouse this API
    /// snapshots but the listener does not watch receives no transfer adjustments all day, and
    /// nothing else anywhere reports that.
    /// </remarks>
    Task<IReadOnlyList<string>> GetMonitoredWarehousesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the listener poll SAP now instead of waiting for its next cycle.
    /// </summary>
    /// <remarks>
    /// A write, despite reading like a query: it advances the listener's poll window, marks documents
    /// processed and delivers webhooks for anything new. Running it twice does not double-post — the
    /// second pass finds the documents already processed — but it is an operator's action and is
    /// gated as one.
    /// </remarks>
    Task<TransferListenerCheckResultDto> TriggerCheckAsync(CancellationToken cancellationToken = default);
}
