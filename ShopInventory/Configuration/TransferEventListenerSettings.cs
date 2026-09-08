namespace ShopInventory.Configuration;

/// <summary>
/// How to reach TransferEventListener, the service that watches SAP for stock transfers and posts
/// them to <c>/api/DesktopIntegration/webhook/transfer-event</c>.
/// </summary>
/// <remarks>
/// Until now the relationship ran one way only: the listener called this API and nothing here ever
/// called back, so a listener that had stopped reading SAP looked exactly like a quiet afternoon.
/// These settings exist for the two calls that close that loop — a health probe, and a second source
/// for the warehouse stock read the morning snapshot depends on.
///
/// The listener holds its own Service Layer session, which is the reason the fallback is worth
/// having: it does not compete for this process's six SAP slots, so it can still answer while every
/// one of them is held by a hung read.
/// </remarks>
public class TransferEventListenerSettings
{
    public const string SectionName = "TransferEventListener";

    /// <summary>
    /// Whether this API may call the listener. Off leaves the inbound webhook working exactly as
    /// before — it is not part of this switch — and skips the health check and the stock fallback.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL of the listener, without a trailing path.</summary>
    public string BaseUrl { get; set; } = "http://10.10.10.9:5050";

    /// <summary>
    /// Budget for an ordinary call. Kept short because every caller has somewhere better to be: the
    /// health probe is polled by monitoring, and the stock fallback is already the second attempt at
    /// a read that has just spent its own budget failing.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether the morning snapshot may fall back to the listener when this API's own non-batch
    /// stock read fails. See <c>FetchDailyStockHandler.FetchUnbatchedStockAsync</c>.
    /// </summary>
    public bool UseForUnbatchedStockFallback { get; set; } = true;

    /// <summary>
    /// Whether a degraded reading — the listener's <c>item-scan-fallback</c> source — is accepted by
    /// that fallback.
    /// </summary>
    /// <remarks>
    /// It covers only the item groups the listener is configured to scan, so it can be short of stock
    /// without saying which. Accepted by default all the same: the alternative on this path is not a
    /// complete snapshot, it is no unbatched rows at all, and a till that can sell four of five
    /// complimentary lines beats one that can sell none. The snapshot records which source answered
    /// so a short reading is visible rather than inferred.
    /// </remarks>
    public bool AcceptDegradedStockFallback { get; set; } = true;

    /// <summary>
    /// How stale the listener's last successful SAP poll may be, in minutes, before the health check
    /// reports it. The listener polls every five minutes by default, so this allows several missed
    /// cycles before anyone is told.
    /// </summary>
    public int PollStalenessWarningMinutes { get; set; } = 20;

    /// <summary>
    /// The point at which a stale poll stops being late and starts being a fault: transfers made this
    /// long ago are still not on the till.
    /// </summary>
    public int PollStalenessCriticalMinutes { get; set; } = 60;
}
