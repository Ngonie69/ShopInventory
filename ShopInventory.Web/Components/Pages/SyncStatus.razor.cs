using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The reading behind /sync-status.
/// </summary>
/// <remarks>
/// Everything here is derived from what the page already fetches — the request
/// log, the cache rows, the queue and the health summary. Nothing new is asked
/// of SAP or the API to draw any of it.
///
/// The derivations exist because the raw numbers do not answer the questions
/// people actually arrive with. "Is SAP slow?" is not answered by one response
/// time — latency against this cluster is heavy-tailed, so a single call says
/// almost nothing and the p50/p95 pair says nearly everything. "Is this cache
/// about to go stale?" is not answered by a timestamp. "Which call is dragging?"
/// is not answered by a chronological log.
/// </remarks>
public partial class SyncStatus : IDisposable
{
    [Inject] private ISyncStatusClientService SyncService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    /// <summary>
    /// How many requests the history reads. Also the window every derived figure
    /// on the page is computed over, which is why the page says so out loud.
    /// </summary>
    private const int LogWindowSize = 50;

    /// <summary>
    /// Refreshing re-probes SAP (the API's connection check calls GetWarehouses),
    /// so the interval stays where it was rather than being tightened to match
    /// the other monitoring pages. The page now states it instead of hiding it.
    /// </summary>
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The age at which a cache is called stale. Mirrors the two-hour rule in
    /// SyncStatusClientService.BuildLocalCacheStatus — the status word comes from
    /// there, and only the "how much of the budget is spent" bar is computed here,
    /// so the two would disagree visibly if that rule ever changed.
    /// </summary>
    private static readonly TimeSpan CacheFreshnessBudget = TimeSpan.FromHours(2);

    /// <summary>
    /// Mirrors OfflineQueueItem.MaxAttempts. The queue DTO does not carry it, and
    /// the attempt pips need something to count against.
    /// </summary>
    private const int QueueMaxAttempts = 5;

    private const int TopEndpointCount = 8;

    private bool isRefreshing;
    private bool isTesting;
    private bool isProcessing;
    private bool isLoading = true;
    private bool autoRefresh = true;
    private bool disposed;

    private string? alertMessage;
    private bool alertSuccess;
    private int alertGeneration;

    private System.Threading.Timer? refreshTimer;
    private DateTime? loadedAtUtc;
    private string currentUsername = "Unknown";
    private string currentUserRole = "User";

    private SapConnectionStatusModel? sapStatus;
    private SyncDashboardModel? dashboard;
    private OfflineQueueStatusModel? queueStatus;
    private List<CacheSyncStatusModel> cacheStatus = new();
    private List<QueuedTransactionModel> queuedTransactions = new();
    private List<ConnectionLogModel> connectionLogs = new();

    /// <summary>The request window, already reduced. Recomputed once per load, not per render.</summary>
    private LinkWindow link = LinkWindow.Empty;

    protected override async Task OnInitializedAsync()
    {
        await LoadCurrentUser();
        await RefreshAll();
        isLoading = false;

        await AuditService.LogAsync("ViewSyncStatus", currentUsername, currentUserRole, "System", null,
            "Accessed sync status page", "/sync-status");

        StartTimer();
    }

    private void StartTimer()
    {
        refreshTimer?.Dispose();
        refreshTimer = new System.Threading.Timer(
            _ => _ = InvokeAsync(async () =>
            {
                // A manual refresh already in flight owns the fields; a second
                // pass would only double the SAP probe.
                if (disposed || isRefreshing)
                {
                    return;
                }

                await RefreshAll();
                StateHasChanged();
            }),
            null,
            AutoRefreshInterval,
            AutoRefreshInterval);
    }

    private void ToggleAutoRefresh(ChangeEventArgs e)
    {
        autoRefresh = e.Value is true;

        if (autoRefresh)
        {
            StartTimer();
        }
        else
        {
            refreshTimer?.Dispose();
            refreshTimer = null;
        }
    }

    private async Task LoadCurrentUser()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        currentUsername = authState.User.Identity?.Name ?? "Unknown";
        currentUserRole = authState.User.Claims
            .FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Role)?.Value ?? "User";
    }

    // ── Loading ─────────────────────────────────────────────────────────────

    private async Task RefreshAll()
    {
        isRefreshing = true;
        try
        {
            await Task.WhenAll(
                RefreshSapStatus(),
                RefreshDashboard(),
                RefreshQueueStatus(),
                RefreshCacheStatus(),
                RefreshQueueItems(),
                RefreshLogs());

            loadedAtUtc = DateTime.UtcNow;
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private async Task RefreshSapStatus()
    {
        try
        {
            sapStatus = await SyncService.GetSapConnectionStatusAsync();
        }
        catch { }
    }

    private async Task RefreshDashboard()
    {
        try
        {
            dashboard = await SyncService.GetSyncDashboardAsync();
        }
        catch { }
    }

    private async Task RefreshQueueStatus()
    {
        try
        {
            queueStatus = await SyncService.GetOfflineQueueStatusAsync();
        }
        catch { }
    }

    private async Task RefreshCacheStatus()
    {
        try
        {
            cacheStatus = await SyncService.GetCacheSyncStatusAsync();
        }
        catch { }
    }

    private async Task RefreshQueueItems()
    {
        try
        {
            queuedTransactions = await SyncService.GetQueuedTransactionsAsync();
        }
        catch { }
    }

    private async Task RefreshLogs()
    {
        try
        {
            connectionLogs = await SyncService.GetConnectionLogsAsync(LogWindowSize);
        }
        catch { }
        finally
        {
            link = LinkWindow.From(connectionLogs);
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    private async Task TestConnection()
    {
        isTesting = true;
        try
        {
            var result = await SyncService.TestSapConnectionAsync();
            ShowAlert(
                result ? "SAP connection succeeded." : "SAP connection failed. Check the configuration and credentials.",
                result);

            // The probe itself is a logged request, so the window moves with it.
            await RefreshSapStatus();
            await RefreshLogs();
        }
        catch (Exception ex)
        {
            ShowAlert($"Connection test failed: {ex.Message}", false);
        }
        finally
        {
            isTesting = false;
        }
    }

    private async Task ProcessQueue()
    {
        isProcessing = true;
        try
        {
            var processed = await SyncService.ProcessQueueAsync();
            ShowAlert(
                processed == 1 ? "Processed 1 queued item." : $"Processed {processed} queued items.",
                true);

            await RefreshQueueStatus();
            await RefreshQueueItems();
            await AuditService.LogAsync("ProcessQueue", currentUsername, currentUserRole, "System", null,
                $"Manually processed {processed} queue items", "/sync-status");
        }
        catch (Exception ex)
        {
            ShowAlert($"Error processing queue: {ex.Message}", false);
        }
        finally
        {
            isProcessing = false;
        }
    }

    private async Task RetryItem(int itemId)
    {
        try
        {
            await SyncService.RetryQueueItemAsync(itemId);
            ShowAlert("Item queued for retry.", true);
            await RefreshQueueItems();
            await RefreshQueueStatus();
        }
        catch (Exception ex)
        {
            ShowAlert($"Error: {ex.Message}", false);
        }
    }

    private async Task CancelItem(int itemId)
    {
        try
        {
            await SyncService.CancelQueueItemAsync(itemId);
            ShowAlert("Item cancelled.", true);
            await RefreshQueueItems();
            await RefreshQueueStatus();
        }
        catch (Exception ex)
        {
            ShowAlert($"Error: {ex.Message}", false);
        }
    }

    /// <summary>
    /// Shows a message for five seconds. The generation counter means a second
    /// message is not cleared early by the first one's timer.
    /// </summary>
    private void ShowAlert(string message, bool success)
    {
        alertMessage = message;
        alertSuccess = success;
        var generation = ++alertGeneration;
        StateHasChanged();

        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            if (disposed || generation != alertGeneration)
            {
                return;
            }

            alertMessage = null;
            _ = InvokeAsync(StateHasChanged);
        });
    }

    // ── SAP link ────────────────────────────────────────────────────────────

    /// <summary>
    /// The link has three states, not two. "Disabled" means the integration was
    /// switched off in configuration — reporting that as "Disconnected", as this
    /// page used to, sends someone to debug a network that was never dialled.
    /// </summary>
    private bool IsSapDisabled => string.Equals(sapStatus?.Status, "Disabled", StringComparison.OrdinalIgnoreCase);

    private bool IsSapConnected => sapStatus?.IsConnected == true;

    private string SapWord => IsSapDisabled ? "Integration off" : IsSapConnected ? "Connected" : "Disconnected";

    private string SapTone => IsSapDisabled ? "syn-fig-warn" : IsSapConnected ? "syn-fig-good" : "syn-fig-danger";

    private string SapStateTone => IsSapDisabled ? "syn-state-warn" : IsSapConnected ? "syn-state-good" : "syn-state-danger";

    private string HealthWord => dashboard?.OverallHealthStatus ?? "Unknown";

    private int HealthScore => dashboard?.HealthSummary?.HealthScore ?? 0;

    private string HealthTone => HealthWord switch
    {
        "Healthy" => "syn-fig-good",
        "Warning" or "Degraded" => "syn-fig-warn",
        "Critical" or "Unhealthy" => "syn-fig-danger",
        _ => string.Empty
    };

    private string HealthMeterTone => HealthWord switch
    {
        "Healthy" => "syn-meter-fill-good",
        "Warning" or "Degraded" => "syn-meter-fill-warn",
        "Critical" or "Unhealthy" => "syn-meter-fill-danger",
        _ => string.Empty
    };

    private IReadOnlyList<string> HealthIssues =>
        dashboard?.HealthSummary?.Issues ?? (IReadOnlyList<string>)Array.Empty<string>();

    private IReadOnlyList<string> HealthAdvice =>
        dashboard?.HealthSummary?.Recommendations ?? (IReadOnlyList<string>)Array.Empty<string>();

    // ── Caches ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Worst first. There are only five of these, so nothing is buried by the
    /// sort, and the one needing attention is never below the fold on a phone.
    /// </summary>
    private IEnumerable<CacheSyncStatusModel> OrderedCaches => cacheStatus
        .OrderBy(cache => cache.Status switch
        {
            "Error" => 0,
            "Unknown" => 1,
            "Stale" => 2,
            "Syncing" => 3,
            _ => 4
        })
        .ThenByDescending(cache => CacheAge(cache) ?? TimeSpan.MaxValue);

    /// <summary>
    /// Anything not currently good: stale, errored, or never synced at all. Kept
    /// in step with <see cref="CacheCardTone"/>, so the count in the card header
    /// matches the number of cards wearing a coloured edge.
    /// </summary>
    private int StaleCacheCount => cacheStatus.Count(cache => !string.IsNullOrEmpty(CacheCardTone(cache)));

    private static TimeSpan? CacheAge(CacheSyncStatusModel cache)
        => cache.LastSyncedAt.HasValue
            ? Clamp(DateTime.UtcNow - EnsureUtc(cache.LastSyncedAt.Value))
            : null;

    /// <summary>How much of the two-hour freshness budget this cache has spent, 0–100.</summary>
    private static int CacheBudgetSpent(CacheSyncStatusModel cache)
    {
        var age = CacheAge(cache);
        if (!age.HasValue)
        {
            return 100;
        }

        var spent = age.Value.TotalMinutes / CacheFreshnessBudget.TotalMinutes * 100;
        return (int)Math.Round(Math.Clamp(spent, 0, 100));
    }

    private static string CacheMeterTone(CacheSyncStatusModel cache) => cache.Status switch
    {
        "Error" or "Unknown" => "syn-meter-fill-danger",
        "Stale" => "syn-meter-fill-warn",
        _ => CacheBudgetSpent(cache) >= 75 ? "syn-meter-fill-warn" : "syn-meter-fill-good"
    };

    private static string CacheCardTone(CacheSyncStatusModel cache) => cache.Status switch
    {
        "Error" or "Unknown" => "syn-cache-bad",
        "Stale" => "syn-cache-late",
        _ => string.Empty
    };

    private static string CacheStateTone(CacheSyncStatusModel cache) => cache.Status switch
    {
        "Synced" => "syn-state-good",
        "Syncing" => "syn-state-busy",
        "Stale" => "syn-state-warn",
        "Error" => "syn-state-danger",
        _ => "syn-state-quiet"
    };

    /// <summary>The right-hand line under the freshness bar: what is left, or how far past.</summary>
    private static string CacheBudgetNote(CacheSyncStatusModel cache)
    {
        var age = CacheAge(cache);
        if (!age.HasValue)
        {
            return "never synced";
        }

        var remaining = CacheFreshnessBudget - age.Value;
        return remaining > TimeSpan.Zero
            ? $"{FormatDuration(remaining)} of budget left"
            : $"{FormatDuration(-remaining)} past due";
    }

    // ── Queue ───────────────────────────────────────────────────────────────

    private int PendingCount => queueStatus?.PendingCount ?? 0;

    private int FailedCount => queueStatus?.FailedCount ?? 0;

    private int ProcessedCount => queueStatus?.ProcessedCount ?? 0;

    private static string QueueStateTone(string status) => status switch
    {
        "Completed" => "syn-state-good",
        "Processing" => "syn-state-busy",
        "Pending" => "syn-state-warn",
        "Failed" => "syn-state-danger",
        _ => "syn-state-quiet"
    };

    private static string AttemptPipClass(QueuedTransactionModel item, int index)
    {
        if (index >= item.AttemptCount)
        {
            return string.Empty;
        }

        return item.AttemptCount >= QueueMaxAttempts ? "syn-pip-out" : "syn-pip-used";
    }

    // ── Formatting ──────────────────────────────────────────────────────────

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static TimeSpan Clamp(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;

    private static string FormatDateTime(DateTime? utcDateTime)
        => utcDateTime.HasValue
            ? $"{IAuditService.ToCAT(EnsureUtc(utcDateTime.Value)):dd MMM HH:mm} CAT"
            : "—";

    private static string FormatClock(DateTime? utcDateTime)
        => utcDateTime.HasValue
            ? $"{IAuditService.ToCAT(EnsureUtc(utcDateTime.Value)):HH:mm:ss} CAT"
            : "—";

    private static string FormatSeconds(DateTime? utcDateTime)
        => utcDateTime.HasValue
            ? $"{IAuditService.ToCAT(EnsureUtc(utcDateTime.Value)):dd MMM HH:mm:ss} CAT"
            : "—";

    /// <summary>Age in the shortest useful form. "2h" beats "127 minutes" at a glance.</summary>
    private static string FormatAge(DateTime? utcDateTime)
        => utcDateTime.HasValue
            ? FormatDuration(Clamp(DateTime.UtcNow - EnsureUtc(utcDateTime.Value)))
            : "—";

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d {span.Hours}h";
        return $"{(int)(span.TotalDays / 7)}w";
    }

    /// <summary>Milliseconds, dropping to seconds once "4,120ms" stops being readable.</summary>
    private static string FormatMs(double? ms)
    {
        if (!ms.HasValue)
        {
            return "—";
        }

        return ms.Value >= 10_000
            ? $"{ms.Value / 1000:N1}s"
            : $"{ms.Value:N0}ms";
    }

    private static string Shorten(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }

    // ── The request window ──────────────────────────────────────────────────

    /// <summary>
    /// One recent request, ready to draw. Failures are drawn full height rather
    /// than by duration: a connection refused in 3ms and a timeout at 30s are
    /// both simply "no answer", and length would rank them backwards.
    /// </summary>
    private sealed record LogBar(bool IsSuccess, bool IsSlow, int HeightPercent, string Title);

    private sealed record EndpointStat(string Name, int Calls, int Failures, double? P50Ms, double? P95Ms);

    /// <summary>
    /// The last <see cref="LogWindowSize"/> requests, reduced to the few numbers
    /// worth acting on. Percentiles cover successful calls only — a failure's
    /// duration measures how fast the error came back, not how long SAP takes.
    /// </summary>
    private sealed record LinkWindow(
        int Total,
        int Failures,
        DateTime? OldestAt,
        DateTime? NewestAt,
        double? P50Ms,
        double? P95Ms,
        double? SlowestMs,
        IReadOnlyList<LogBar> Bars,
        IReadOnlyList<EndpointStat> Endpoints)
    {
        public static readonly LinkWindow Empty = new(
            0, 0, null, null, null, null, null,
            Array.Empty<LogBar>(), Array.Empty<EndpointStat>());

        public bool HasData => Total > 0;

        public int Successes => Total - Failures;

        public double SuccessRate => Total == 0 ? 0 : (double)Successes / Total * 100;

        public TimeSpan? Span => OldestAt.HasValue && NewestAt.HasValue
            ? NewestAt.Value - OldestAt.Value
            : null;

        public static LinkWindow From(IReadOnlyList<ConnectionLogModel> logs)
        {
            if (logs.Count == 0)
            {
                return Empty;
            }

            // The API hands them back newest first; the chart reads left to right.
            var ordered = logs.OrderBy(log => log.CheckedAt).ToList();
            var failures = ordered.Count(log => !log.IsSuccess);

            var successMs = ordered
                .Where(log => log.IsSuccess && log.ResponseTimeMs.HasValue)
                .Select(log => log.ResponseTimeMs!.Value)
                .OrderBy(ms => ms)
                .ToList();

            var p50 = Percentile(successMs, 50);
            var p95 = Percentile(successMs, 95);
            var slowest = successMs.Count > 0 ? successMs[^1] : (double?)null;

            // Bars are scaled to p95, not to the slowest call: latency here is
            // heavy-tailed, and one 30-second outlier would flatten every other
            // bar to a pixel. Anything at or beyond p95 draws full height.
            var scale = p95 is > 0 ? p95.Value : slowest ?? 1;

            var bars = ordered.Select(log =>
            {
                if (!log.IsSuccess)
                {
                    return new LogBar(false, false, 100,
                        $"{FormatClock(log.CheckedAt)} · {log.Endpoint ?? "unknown"} · failed");
                }

                var ms = log.ResponseTimeMs ?? 0;
                // Floored at 5% so a fast call still draws as a bar rather than a
                // hairline — the baseline's steadiness is half of what the shape says.
                var height = (int)Math.Round(Math.Clamp(ms / scale * 100, 5, 100));
                var isSlow = p95.HasValue && ms >= p95.Value && successMs.Count > 2;

                return new LogBar(true, isSlow, height,
                    $"{FormatClock(log.CheckedAt)} · {log.Endpoint ?? "unknown"} · {FormatMs(ms)}");
            }).ToList();

            var endpoints = ordered
                .GroupBy(log => NormaliseEndpoint(log.Endpoint), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupMs = group
                        .Where(log => log.IsSuccess && log.ResponseTimeMs.HasValue)
                        .Select(log => log.ResponseTimeMs!.Value)
                        .OrderBy(ms => ms)
                        .ToList();

                    return new EndpointStat(
                        group.Key,
                        group.Count(),
                        group.Count(log => !log.IsSuccess),
                        Percentile(groupMs, 50),
                        Percentile(groupMs, 95));
                })
                // Anything failing is the story; after that, whatever is slowest.
                .OrderByDescending(stat => stat.Failures)
                .ThenByDescending(stat => stat.P95Ms ?? 0)
                .ThenByDescending(stat => stat.Calls)
                .Take(TopEndpointCount)
                .ToList();

            return new LinkWindow(
                ordered.Count,
                failures,
                EnsureUtc(ordered[0].CheckedAt),
                EnsureUtc(ordered[^1].CheckedAt),
                p50,
                p95,
                slowest,
                bars,
                endpoints);
        }

        /// <summary>Nearest-rank percentile over an already-sorted list.</summary>
        private static double? Percentile(IReadOnlyList<double> sorted, int percentile)
        {
            if (sorted.Count == 0)
            {
                return null;
            }

            var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
            return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
        }

        /// <summary>
        /// "GET Items('A1000')" and "GET Items('B2000')" are the same call made
        /// twice, so the key drops anything inside parentheses. Without this the
        /// breakdown degenerates into one row per document.
        /// </summary>
        private static string NormaliseEndpoint(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return "Unknown";
            }

            var segments = endpoint.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var trimmed = segments.Select(segment =>
            {
                var open = segment.IndexOf('(');
                return open < 0 ? segment : segment[..open];
            });

            return string.Join('/', trimmed);
        }
    }

    public void Dispose()
    {
        disposed = true;
        refreshTimer?.Dispose();
    }
}
