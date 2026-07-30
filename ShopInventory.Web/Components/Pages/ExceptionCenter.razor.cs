using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.ExceptionCenter.Commands.AcknowledgeExceptionCenterItem;
using ShopInventory.Web.Features.ExceptionCenter.Commands.AssignExceptionCenterItem;
using ShopInventory.Web.Features.ExceptionCenter.Commands.RetryExceptionCenterBatch;
using ShopInventory.Web.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;
using ShopInventory.Web.Features.ExceptionCenter.Queries.GetExceptionCenter;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class ExceptionCenter : IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] private ILogger<ExceptionCenter> Logger { get; set; } = default!;

    private const int PageLimit = 200;
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(60);

    private ExceptionCenterDashboardModel? dashboard;
    private bool isLoading = true;
    private bool isRefreshing;
    private bool hasInitialized;
    private bool hasLoggedView;
    private string? errorMessage;
    private string? successMessage;
    private string currentUsername = string.Empty;

    // Filters. The preset is the primary control — an operator almost always wants
    // one of these five slices rather than a hand-built combination.
    private string preset = PresetNeedsHuman;
    private string searchText = string.Empty;
    private string selectedSource = "All";
    private string selectedFamily = "All";
    private string sortOrder = SortOldest;
    private string? focusedCluster;

    private const string PresetAll = "All";
    private const string PresetNeedsHuman = "NeedsHuman";
    private const string PresetStalled = "Stalled";
    private const string PresetRetrying = "Retrying";
    private const string PresetMine = "Mine";
    private const string PresetUnassigned = "Unassigned";

    private const string SortOldest = "Oldest";
    private const string SortNewest = "Newest";
    private const string SortValue = "Value";

    private const string TriageBlocked = "Blocked";
    private const string TriageRetrying = "Retrying";
    private const string TriageStalled = "Stalled";

    private readonly HashSet<string> expandedRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedClusters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> acknowledgingKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> assigningKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> retryingKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> retryingClusters = new(StringComparer.OrdinalIgnoreCase);

    private bool autoRefresh;
    private Timer? autoRefreshTimer;

    private ExceptionCenterTriageModel Triage => dashboard?.Triage ?? new ExceptionCenterTriageModel();
    private List<ExceptionCenterItemModel> Items => dashboard?.Items ?? [];
    private List<ExceptionCenterClusterModel> Clusters => dashboard?.Clusters ?? [];
    private List<ExceptionCenterSourceModel> Sources => dashboard?.Sources ?? [];
    private List<ExceptionCenterExposureModel> Exposure => dashboard?.Exposure ?? [];
    private List<ExceptionCenterTrendPointModel> Trend => dashboard?.Trend ?? [];
    private List<ExceptionCenterItemModel> FilteredItems => ApplyFilters().ToList();

    private bool HasAnything => Items.Count > 0 || (dashboard?.TotalItemCount ?? 0) > 0;

    private List<string> SourceOptions =>
        new[] { "All" }
            .Concat(Items.Select(item => item.Source)
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(source => source, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private List<string> FamilyOptions =>
        new[] { "All" }
            .Concat(Items.Select(item => item.Family ?? "Unknown")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(family => family, StringComparer.OrdinalIgnoreCase))
            .ToList();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
        {
            return;
        }

        hasInitialized = true;
        await ResolveCurrentUserAsync();
        await LoadDashboardAsync();

        // The default view is the work that needs a person. If there is none but the
        // queues are not empty, open on everything rather than on a blank table.
        if (Triage.BlockedCount == 0 && Items.Count > 0)
        {
            preset = PresetAll;
        }

        StateHasChanged();
    }

    private async Task LoadDashboardAsync(bool silent = false)
    {
        if (silent)
        {
            isRefreshing = true;
        }
        else
        {
            isLoading = true;
            errorMessage = null;
            successMessage = null;
        }

        try
        {
            var result = await Mediator.Send(new GetExceptionCenterQuery(PageLimit, currentUsername));

            result.SwitchFirst(
                value => dashboard = value,
                error => errorMessage = error.Description);

            if (!result.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
                await AuditService.LogAsync(AuditActions.ViewExceptionCenter, "ExceptionCenter", null, "Viewed integration exception queue", true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load exception center page");
            errorMessage = "Failed to load exception center.";
        }
        finally
        {
            isLoading = false;
            isRefreshing = false;
        }
    }

    private Task ReloadAsync() => LoadDashboardAsync();

    // ── Auto refresh ────────────────────────────────────────────────────────

    private void ToggleAutoRefresh(ChangeEventArgs args)
    {
        autoRefresh = args.Value is true;

        if (!autoRefresh)
        {
            autoRefreshTimer?.Dispose();
            autoRefreshTimer = null;
            return;
        }

        autoRefreshTimer = new Timer(_ => _ = RefreshOnTimerAsync(), null, AutoRefreshInterval, AutoRefreshInterval);
    }

    /// <summary>
    /// The timer callback cannot be awaited, so anything it throws would go unobserved —
    /// hence the catch. A failed background refresh leaves the last good view in place.
    /// </summary>
    private async Task RefreshOnTimerAsync()
    {
        try
        {
            await InvokeAsync(async () =>
            {
                await LoadDashboardAsync(silent: true);
                StateHasChanged();
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Exception center auto-refresh failed");
        }
    }

    public void Dispose()
    {
        autoRefreshTimer?.Dispose();
        autoRefreshTimer = null;
    }

    // ── Item actions ────────────────────────────────────────────────────────

    private async Task RetryItemAsync(ExceptionCenterItemModel item)
    {
        var retryKey = GetItemKey(item);
        if (!retryingKeys.Add(retryKey))
        {
            return;
        }

        errorMessage = null;
        successMessage = null;

        try
        {
            var result = await Mediator.Send(new RetryExceptionCenterItemCommand(item.Source, item.ItemId));
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                await AuditService.LogAsync(AuditActions.RetryExceptionCenterItem, "ExceptionCenter", retryKey, result.FirstError.Description, false, result.FirstError.Description);
                return;
            }

            successMessage = $"Retry queued for {item.Reference}.";
            await AuditService.LogAsync(AuditActions.RetryExceptionCenterItem, "ExceptionCenter", retryKey, $"Retried {item.Reference}", true);
            await LoadDashboardAsync(silent: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to retry exception center item {Source}:{ItemId}", item.Source, item.ItemId);
            errorMessage = "Retry request failed.";
        }
        finally
        {
            retryingKeys.Remove(retryKey);
        }
    }

    /// <summary>
    /// Retries a whole root cause. One SAP fix usually unblocks the entire group, so
    /// the group is the natural unit of work rather than the row.
    /// </summary>
    private async Task RetryClusterAsync(ExceptionCenterClusterModel cluster)
    {
        if (cluster.RetryableItems.Count == 0 || !retryingClusters.Add(cluster.Signature))
        {
            return;
        }

        errorMessage = null;
        successMessage = null;

        try
        {
            var result = await Mediator.Send(new RetryExceptionCenterBatchCommand(cluster.RetryableItems));
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                await AuditService.LogAsync(AuditActions.RetryExceptionCenterBatch, "ExceptionCenter", cluster.Signature, result.FirstError.Description, false, result.FirstError.Description);
                return;
            }

            var outcome = result.Value;
            successMessage = outcome.FailedCount == 0
                ? $"Requeued {outcome.RetriedCount} item(s) for “{cluster.Label}”."
                : $"Requeued {outcome.RetriedCount} of {outcome.RequestedCount} item(s); {outcome.FailedCount} could not be retried.";

            if (outcome.FailedCount > 0 && outcome.Failures.Count > 0)
            {
                errorMessage = string.Join(" · ", outcome.Failures);
            }

            await AuditService.LogAsync(
                AuditActions.RetryExceptionCenterBatch,
                "ExceptionCenter",
                cluster.Signature,
                $"Batch retried {outcome.RetriedCount}/{outcome.RequestedCount} items for {cluster.Label}",
                outcome.FailedCount == 0);

            await LoadDashboardAsync(silent: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to batch retry exception center cluster {Signature}", cluster.Signature);
            errorMessage = "Batch retry request failed.";
        }
        finally
        {
            retryingClusters.Remove(cluster.Signature);
        }
    }

    private async Task AcknowledgeItemAsync(ExceptionCenterItemModel item)
    {
        var itemKey = GetItemKey(item);
        if (!acknowledgingKeys.Add(itemKey))
        {
            return;
        }

        errorMessage = null;
        successMessage = null;

        try
        {
            var result = await Mediator.Send(new AcknowledgeExceptionCenterItemCommand(item.Source, item.ItemId));
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                await AuditService.LogAsync(AuditActions.AcknowledgeExceptionCenterItem, "ExceptionCenter", itemKey, result.FirstError.Description, false, result.FirstError.Description);
                return;
            }

            successMessage = $"Acknowledged {item.Reference}.";
            await AuditService.LogAsync(AuditActions.AcknowledgeExceptionCenterItem, "ExceptionCenter", itemKey, $"Acknowledged {item.Reference}", true);
            await LoadDashboardAsync(silent: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to acknowledge exception center item {Source}:{ItemId}", item.Source, item.ItemId);
            errorMessage = "Acknowledge request failed.";
        }
        finally
        {
            acknowledgingKeys.Remove(itemKey);
        }
    }

    private async Task AssignItemAsync(ExceptionCenterItemModel item)
    {
        var itemKey = GetItemKey(item);
        if (!assigningKeys.Add(itemKey))
        {
            return;
        }

        errorMessage = null;
        successMessage = null;

        try
        {
            var result = await Mediator.Send(new AssignExceptionCenterItemCommand(item.Source, item.ItemId));
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                await AuditService.LogAsync(AuditActions.AssignExceptionCenterItem, "ExceptionCenter", itemKey, result.FirstError.Description, false, result.FirstError.Description);
                return;
            }

            successMessage = $"Assigned {item.Reference} to {currentUsername}.";
            await AuditService.LogAsync(AuditActions.AssignExceptionCenterItem, "ExceptionCenter", itemKey, $"Assigned {item.Reference} to {currentUsername}", true);
            await LoadDashboardAsync(silent: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to assign exception center item {Source}:{ItemId}", item.Source, item.ItemId);
            errorMessage = "Assign request failed.";
        }
        finally
        {
            assigningKeys.Remove(itemKey);
        }
    }

    private async Task ResolveCurrentUserAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        currentUsername = authState.User.Identity?.Name ?? string.Empty;
    }

    // ── Filtering ───────────────────────────────────────────────────────────

    private IEnumerable<ExceptionCenterItemModel> ApplyFilters()
    {
        IEnumerable<ExceptionCenterItemModel> query = Items;

        query = preset switch
        {
            PresetNeedsHuman => query.Where(item => item.Triage == TriageBlocked),
            PresetStalled => query.Where(item => item.Triage == TriageStalled || item.IsRetryOverdue),
            PresetRetrying => query.Where(item => item.Triage == TriageRetrying),
            PresetMine => query.Where(item => string.Equals(item.AssignedToUsername, currentUsername, StringComparison.OrdinalIgnoreCase)),
            PresetUnassigned => query.Where(item => string.IsNullOrWhiteSpace(item.AssignedToUsername)),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(focusedCluster))
        {
            query = query.Where(item => string.Equals(item.ClusterSignature, focusedCluster, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(selectedSource, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item => string.Equals(item.Source, selectedSource, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(selectedFamily, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item => string.Equals(item.Family ?? "Unknown", selectedFamily, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query = query.Where(item => MatchesSearch(item, searchText));
        }

        return sortOrder switch
        {
            SortNewest => query.OrderByDescending(item => item.OccurredAtUtc ?? item.CreatedAtUtc),
            SortValue => query.OrderByDescending(item => item.Amount ?? 0m)
                              .ThenBy(item => item.OccurredAtUtc ?? item.CreatedAtUtc),
            _ => query.OrderBy(item => item.OccurredAtUtc ?? item.CreatedAtUtc)
        };
    }

    private void SetPreset(string value)
    {
        preset = value;
        focusedCluster = null;
    }

    private void FocusCluster(string? signature)
    {
        focusedCluster = string.Equals(focusedCluster, signature, StringComparison.OrdinalIgnoreCase)
            ? null
            : signature;

        // A cluster spans both blocked and retrying items, so widen the preset or the
        // focus would silently hide most of the group.
        if (focusedCluster is not null)
        {
            preset = PresetAll;
        }
    }

    private void ResetFilters()
    {
        preset = PresetNeedsHuman;
        searchText = string.Empty;
        selectedSource = "All";
        selectedFamily = "All";
        sortOrder = SortOldest;
        focusedCluster = null;
    }

    private void ToggleRow(ExceptionCenterItemModel item)
    {
        var key = GetItemKey(item);
        if (!expandedRows.Remove(key))
        {
            expandedRows.Add(key);
        }
    }

    private void ToggleCluster(string signature)
    {
        if (!expandedClusters.Remove(signature))
        {
            expandedClusters.Add(signature);
        }
    }

    private static bool MatchesSearch(ExceptionCenterItemModel item, string search)
        => Contains(item.Reference, search)
           || Contains(item.Title, search)
           || Contains(item.LastError, search)
           || Contains(item.RootCause, search)
           || Contains(item.Source, search)
           || Contains(item.Category, search)
           || Contains(item.Counterparty, search)
           || Contains(item.Location, search)
           || Contains(item.AssignedToUsername, search);

    private static bool Contains(string? value, string search)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    // ── Presentation helpers ────────────────────────────────────────────────

    private bool CanAssignToCurrentUser(ExceptionCenterItemModel item)
        => !string.IsNullOrWhiteSpace(currentUsername)
           && !string.Equals(item.AssignedToUsername, currentUsername, StringComparison.OrdinalIgnoreCase);

    private static string GetItemKey(ExceptionCenterItemModel item) => $"{item.Source}:{item.ItemId}";

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string FormatDateTime(DateTime? utcDateTime)
        => utcDateTime.HasValue
            ? $"{IAuditService.ToCAT(EnsureUtc(utcDateTime.Value)):dd MMM HH:mm} CAT"
            : "—";

    private static string FormatTime(DateTime? utcDateTime)
        => utcDateTime.HasValue
            ? $"{IAuditService.ToCAT(EnsureUtc(utcDateTime.Value)):HH:mm} CAT"
            : "—";

    /// <summary>How long a failure has been sitting there, in the shortest useful form.</summary>
    private static string FormatAge(DateTime? utcDateTime)
    {
        if (!utcDateTime.HasValue)
        {
            return "—";
        }

        var span = DateTime.UtcNow - EnsureUtc(utcDateTime.Value);
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalMinutes < 1)
        {
            return "just now";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes}m";
        }

        if (span.TotalDays < 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return span.TotalDays < 30
            ? $"{(int)span.TotalDays}d {span.Hours}h"
            : $"{(int)(span.TotalDays / 30)}mo";
    }

    private static string FormatCountdown(DateTime? utcDateTime)
    {
        if (!utcDateTime.HasValue)
        {
            return "—";
        }

        var span = EnsureUtc(utcDateTime.Value) - DateTime.UtcNow;
        if (span <= TimeSpan.Zero)
        {
            return "due now";
        }

        return span.TotalHours < 1
            ? $"in {Math.Max(1, (int)span.TotalMinutes)}m"
            : span.TotalDays < 1
                ? $"in {(int)span.TotalHours}h"
                : $"in {(int)span.TotalDays}d";
    }

    private static string AgeClass(DateTime? utcDateTime)
    {
        if (!utcDateTime.HasValue)
        {
            return string.Empty;
        }

        var span = DateTime.UtcNow - EnsureUtc(utcDateTime.Value);
        return span.TotalDays >= 7 ? "exc-age-stale" : span.TotalHours >= 24 ? "exc-age-old" : string.Empty;
    }

    private static string FormatMoney(decimal amount, string? currency)
        => $"{(string.IsNullOrWhiteSpace(currency) ? string.Empty : currency + " ")}{amount.ToString("N2", CultureInfo.InvariantCulture)}";

    private static string FormatExposure(ExceptionCenterExposureModel entry)
        => entry.Currency is not null
            ? FormatMoney(entry.Amount, entry.Currency)
            : $"{entry.Amount.ToString("N0", CultureInfo.InvariantCulture)} {entry.Unit}";

    private static string TriageBadgeClass(string triage)
        => triage switch
        {
            TriageBlocked => "exc-badge-blocked",
            TriageStalled => "exc-badge-stalled",
            _ => "exc-badge-retrying"
        };

    private static string TriageLabel(string triage)
        => triage switch
        {
            TriageBlocked => "Needs a human",
            TriageStalled => "Stalled",
            _ => "Retrying"
        };

    private static string TriageIcon(string triage)
        => triage switch
        {
            TriageBlocked => "ph ph-hand-palm",
            TriageStalled => "ph ph-pause-circle",
            _ => "ph ph-arrows-clockwise"
        };

    private static string FamilyClass(string? family)
        => "exc-fam-" + (family ?? "Unknown").ToLowerInvariant();

    private static string FamilyIcon(string? family)
        => family switch
        {
            "Sap" => "ph ph-database",
            "Connectivity" => "ph ph-plugs",
            "Fiscal" => "ph ph-receipt",
            "Stock" => "ph ph-package",
            "Data" => "ph ph-warning-diamond",
            "Payment" => "ph ph-credit-card",
            "Worker" => "ph ph-cpu",
            _ => "ph ph-question"
        };

    private static string DescribeSource(string source)
        => source switch
        {
            "invoice-queue" => "Invoice posting queue",
            "inventory-transfer-queue" => "Inventory transfer queue",
            "mobile-order-post-processing" => "Mobile order post-processing",
            "incoming-payment-queue" => "Customer receipt queue",
            "payment-callback" => "Payment gateway callbacks",
            "payment-callback-rejection" => "Rejected payment callbacks",
            "credit-note-fiscalization" => "Credit note fiscalization",
            _ => source
        };

    private int PresetCount(string value)
        => value switch
        {
            PresetNeedsHuman => Triage.BlockedCount,
            PresetStalled => Items.Count(item => item.Triage == TriageStalled || item.IsRetryOverdue),
            PresetRetrying => Triage.RetryingCount,
            PresetMine => Triage.AssignedToMeCount,
            PresetUnassigned => Items.Count(item => string.IsNullOrWhiteSpace(item.AssignedToUsername)),
            _ => Items.Count
        };

    /// <summary>Trend bars are scaled against the busiest hour in the window.</summary>
    private int TrendPeak => Trend.Count == 0 ? 0 : Trend.Max(point => point.Count);

    private int TrendBarHeight(int count)
        => TrendPeak == 0 ? 2 : Math.Max(2, (int)Math.Round(count / (double)TrendPeak * 100));

    private static string Shorten(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var collapsed = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return collapsed.Length <= limit ? collapsed : collapsed[..limit].TrimEnd() + "…";
    }

    private static int AgingPercent(int part, int total)
        => total <= 0 ? 0 : (int)Math.Round(part / (double)total * 100);
}
