using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Common;
using ShopInventory.Web.Components.Dashboard;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The administrator's dashboard. Home renders it at /dashboard for an Admin,
/// the way it renders <see cref="SalesRepDashboard"/> for a rep.
///
/// Every figure is nullable until its read lands, so the page shows "—" rather
/// than a zero that is about to jump — a zero here would read as "nothing is
/// failing", which is the opposite of not knowing yet.
///
/// Each read is caught separately so one dead service cannot blank the page,
/// and each catch logs. The page this replaced swallowed five exceptions
/// silently, which is how a dead endpoint came to look like a quiet day.
/// </summary>
public partial class AdminDashboard
{
    [Inject] private ISystemHealthService HealthService { get; set; } = default!;
    [Inject] private IExceptionCenterService ExceptionService { get; set; } = default!;
    [Inject] private IInventoryTransferService TransferService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private IUserManagementService UserService { get; set; } = default!;
    [Inject] private IInvoiceService InvoiceService { get; set; } = default!;
    [Inject] private IPaymentService PaymentService { get; set; } = default!;
    [Inject] private ILogger<AdminDashboard> Logger { get; set; } = default!;

    /// <summary>The name to greet. Home resolves it from the signed-in user.</summary>
    [Parameter, EditorRequired] public string DisplayName { get; set; } = "there";

    /// <summary>
    /// How many failure clusters the panel shows. The exception centre ranks
    /// them, so the top few are the ones worth a decision.
    /// </summary>
    private const int ClustersShown = 5;

    /// <summary>
    /// How deep to read the exception centre. Only the triage counts and the
    /// ranked clusters are drawn here, so the item list is asked for at the
    /// smallest size the call accepts.
    /// </summary>
    private const int ExceptionReadLimit = 20;

    private const int ActivityRowsShown = 6;

    /// <summary>When the figures on screen were read. Null until the first read lands.</summary>
    private DateTime? loadedAt;

    private string? healthStatus;
    private string? dependenciesStatus;
    private List<SystemHealthCheckEntry> healthChecks = [];

    private int? blockedCount;
    private int? stalledCount;
    private int? retryOverdueCount;
    private DateTime? oldestOpenAtUtc;
    private int? ageOver7dCount;
    private List<ExceptionCenterClusterModel>? clusters;
    private bool isLoadingExceptions = true;
    private bool exceptionsUnavailable;

    private int? pendingTransferCount;
    private int? postFailedCount;

    private int? activeUsers;
    private int? totalActions;
    private int? failedActions;

    private int? lockedUsers;
    private int? usersWithoutTwoFactor;

    private int? todayInvoiceCount;
    private int? todayPaymentCount;
    private decimal invoiceTotal;
    private decimal paymentTotal;
    private (string? Text, int Direction) invoiceTrend;
    private (string? Text, int Direction) paymentTrend;

    private List<AuditLog>? recentActivity;
    private bool isLoadingActivity = true;

    private bool _initialized;

    protected override async Task OnInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await Task.WhenAll(
            LoadHealthAsync(),
            LoadExceptionsAsync(),
            LoadTransferQueuesAsync(),
            LoadActivityStatsAsync(),
            LoadSecurityStatsAsync(),
            LoadRecentActivityAsync(),
            LoadInvoiceStatsAsync(),
            LoadPaymentStatsAsync());

        // Stamped once every read has landed, so the header's time describes
        // the whole page rather than whichever call returned first.
        loadedAt = DateTime.Now;
    }

    private async Task LoadHealthAsync()
    {
        try
        {
            var health = await HealthService.GetHealthAsync();
            if (health != null)
            {
                healthStatus = health.Status;
                dependenciesStatus = health.Dependencies?.Status;
                healthChecks = health.Dependencies?.Checks ?? [];
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard could not read system health.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadExceptionsAsync()
    {
        try
        {
            var dashboard = await ExceptionService.GetDashboardAsync(limit: ExceptionReadLimit);
            if (dashboard != null)
            {
                blockedCount = dashboard.Triage.BlockedCount;
                stalledCount = dashboard.Triage.StalledCount;
                retryOverdueCount = dashboard.Triage.RetryOverdueCount;
                oldestOpenAtUtc = dashboard.Triage.OldestOpenAtUtc;
                ageOver7dCount = dashboard.Triage.AgeOver7dCount;

                clusters = dashboard.Clusters
                    .OrderByDescending(cluster => cluster.Count)
                    .Take(ClustersShown)
                    .ToList();
            }
            else
            {
                exceptionsUnavailable = true;
            }
        }
        catch (Exception ex)
        {
            exceptionsUnavailable = true;
            Logger.LogWarning(ex, "Dashboard could not read the exception centre.");
        }
        finally
        {
            isLoadingExceptions = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadTransferQueuesAsync()
    {
        // Only the totals are drawn, so both reads ask for the smallest page
        // that still carries an authoritative TotalCount.
        pendingTransferCount = await CountTransfersAsync(PendingTransferStatuses.AwaitingApproval);
        postFailedCount = await CountTransfersAsync(PendingTransferStatuses.PostFailed);
        await InvokeAsync(StateHasChanged);
    }

    private async Task<int?> CountTransfersAsync(string status)
    {
        try
        {
            var response = await TransferService.GetPendingTransfersAsync(status, pageSize: 1);
            return response?.TotalCount;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard could not count {Status} transfers.", status);
            return null;
        }
    }

    private async Task LoadActivityStatsAsync()
    {
        try
        {
            var stats = await AuditService.GetActivityStatsAsync(DateTime.Today, DateTime.Today.AddDays(1));
            activeUsers = stats.ActiveUsers;
            totalActions = stats.TotalActions;
            failedActions = stats.FailedActions;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard could not read today's activity statistics.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadSecurityStatsAsync()
    {
        try
        {
            var stats = await UserService.GetSecurityStatsAsync();
            lockedUsers = stats.LockedUsers;
            usersWithoutTwoFactor = stats.UsersWithoutTwoFactor;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard could not read security statistics.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadRecentActivityAsync()
    {
        try
        {
            var page = await AuditService.GetLogPageAsync(page: 1, pageSize: ActivityRowsShown);
            recentActivity = page.Items;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard could not read the recent activity log.");
        }
        finally
        {
            isLoadingActivity = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadInvoiceStatsAsync()
    {
        try
        {
            var today = DateTime.Today;
            var todayTask = DashboardFigures.GetInvoiceDayTotalsAsync(InvoiceService, today, includeValue: true);
            var yesterdayTask = DashboardFigures.GetInvoiceDayTotalsAsync(InvoiceService, today.AddDays(-1), includeValue: false);
            await Task.WhenAll(todayTask, yesterdayTask);

            var (count, total) = await todayTask;
            var (yesterdayCount, _) = await yesterdayTask;

            todayInvoiceCount = count;
            invoiceTotal = total;
            invoiceTrend = DashboardFigures.BuildTrend(count, yesterdayCount);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard could not read today's invoice figures.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadPaymentStatsAsync()
    {
        try
        {
            var today = DateTime.Today;
            var todayTask = DashboardFigures.GetPaymentDayTotalsAsync(PaymentService, today);
            var yesterdayTask = DashboardFigures.GetPaymentDayTotalsAsync(PaymentService, today.AddDays(-1));
            await Task.WhenAll(todayTask, yesterdayTask);

            var (count, total) = await todayTask;
            var (yesterdayCount, _) = await yesterdayTask;

            todayPaymentCount = count;
            paymentTotal = total;
            paymentTrend = DashboardFigures.BuildTrend(count, yesterdayCount);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard could not read today's payment figures.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    // ── Presentation ────────────────────────────────────────────────────────

    private static string GreetingText => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening"
    };

    /// <summary>A figure that has not landed yet reads as a dash, not a zero.</summary>
    private static string Figure(int? value) => value?.ToString("N0") ?? "—";

    private static string FormatMoney(decimal value) => $"USD {value:N2}";

    private static string ActivityDetail(AuditLog entry) =>
        string.IsNullOrWhiteSpace(entry.Details) ? entry.EntityType ?? entry.Action : entry.Details;

    private IEnumerable<SystemHealthCheckEntry> FailingChecks =>
        healthChecks.Where(check => !SystemHealthSummary.IsPassing(check.Status));

    /// <summary>
    /// The label and the judgement, from <see cref="SystemHealthSummary"/> —
    /// which exists because reading the report's top-level status on its own
    /// reports "Healthy" through a SAP outage.
    /// </summary>
    private (string Value, bool IsHealthy) Health =>
        SystemHealthSummary.Describe(
            healthStatus,
            dependenciesStatus,
            healthChecks.Select(check => check.Status));

    private string HealthValue => Health.Value;

    private StatTone HealthTone => healthStatus is null
        ? StatTone.Neutral
        : Health.IsHealthy ? StatTone.Ok : StatTone.Critical;

    private string? HealthNote
    {
        get
        {
            if (healthStatus is null) return null;

            var failing = FailingChecks.Select(check => check.Name).ToList();
            if (failing.Count == 0)
            {
                return healthChecks.Count == 0
                    ? "No dependency checks reported"
                    : $"All {healthChecks.Count} checks passing";
            }

            return failing.Count <= 2
                ? string.Join(", ", failing)
                : $"{failing.Count} checks failing";
        }
    }

    private StatTone BlockedTone => blockedCount switch
    {
        null => StatTone.Neutral,
        0 => StatTone.Ok,
        _ when ageOver7dCount > 0 => StatTone.Critical,
        _ => StatTone.Warn
    };

    private string? BlockedNote
    {
        get
        {
            if (blockedCount is null) return null;
            if (blockedCount == 0) return "Nothing blocked";

            if (oldestOpenAtUtc is { } oldest)
            {
                return $"Oldest {DescribeAge(DateTime.UtcNow - oldest)}";
            }

            return "Needs a decision";
        }
    }

    private StatTone RetryTone => retryOverdueCount switch
    {
        null => StatTone.Neutral,
        0 when stalledCount is 0 => StatTone.Ok,
        0 => StatTone.Neutral,
        _ => StatTone.Warn
    };

    private string? RetryNote => retryOverdueCount switch
    {
        null => null,
        0 when stalledCount is 0 => "Retrying normally",
        0 => null,
        _ => "Past their retry time"
    };

    private StatTone PostFailedTone => postFailedCount switch
    {
        null => StatTone.Neutral,
        0 => StatTone.Ok,
        _ => StatTone.Critical
    };

    private string? PostFailedNote => postFailedCount switch
    {
        null => null,
        0 => "None stranded",
        _ => "Stranded, need a retry"
    };

    private StatTone ActivityTone =>
        failedActions is null or 0 ? StatTone.Neutral : StatTone.Warn;

    private string? ActivityNote =>
        failedActions is null or 0 ? null : $"{failedActions:N0} failed";

    private StatTone SecurityTone => lockedUsers switch
    {
        null => StatTone.Neutral,
        > 0 => StatTone.Warn,
        _ when usersWithoutTwoFactor > 0 => StatTone.Warn,
        _ => StatTone.Ok
    };

    private string? SecurityNote
    {
        get
        {
            if (lockedUsers is null) return null;

            return usersWithoutTwoFactor switch
            {
                null or 0 => lockedUsers > 0 ? "Locked out of the app" : "All accounts open, 2FA everywhere",
                _ => $"{usersWithoutTwoFactor:N0} without 2FA"
            };
        }
    }

    /// <summary>Coarse age, because the card has room for two words.</summary>
    private static string DescribeAge(TimeSpan age) => age switch
    {
        { TotalMinutes: < 60 } => $"{Math.Max(1, (int)age.TotalMinutes)}m",
        { TotalHours: < 24 } => $"{(int)age.TotalHours}h",
        _ => $"{(int)age.TotalDays}d"
    };
}
