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
    [Inject] private ISalesOrderService SalesOrderService { get; set; } = default!;
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

    // The document counts are all scoped to today, including the second figure each card carries.
    // A queue counted over all time next to a count of one day reads as a proportion of it, and
    // would be a badly wrong one; the pages these cards link to are where the standing queues live.
    private int? todayMobileOrderCount;
    private int? mobileOrdersToReview;
    private (string? Text, int Direction) mobileOrderTrend;

    private int? todaySalesOrderCount;
    private int? salesOrdersToApprove;
    private (string? Text, int Direction) salesOrderTrend;

    private int? todayTransferCount;
    private int? todayTransfersPosted;
    private (string? Text, int Direction) transferTrend;

    private List<AuditLog>? recentActivity;
    private bool isLoadingActivity = true;

    /// <summary>
    /// The failure family the clusters panel is filtered to, or null for all of
    /// them. Held here rather than derived, because the filter has to survive a
    /// refresh landing underneath it.
    /// </summary>
    private string? familyFilter;

    private bool isRefreshing;

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
            LoadPaymentStatsAsync(),
            LoadMobileOrderStatsAsync(),
            LoadSalesOrderStatsAsync(),
            LoadTransferDayStatsAsync());

        // Stamped once every read has landed, so the header's time describes
        // the whole page rather than whichever call returned first.
        loadedAt = DateTime.Now;
    }

    /// <summary>
    /// Reads every figure again. The page does not poll — the header states
    /// when it last read, and this is how that stamp is moved on.
    ///
    /// The panels are put back into their loading state first, so a slow read
    /// shows a spinner rather than leaving yesterday's rows on screen looking
    /// current. The family filter is deliberately kept: it is the reader's
    /// choice, not part of the data.
    /// </summary>
    private async Task RefreshAsync()
    {
        if (isRefreshing) return;

        isRefreshing = true;
        loadedAt = null;
        isLoadingExceptions = true;
        isLoadingActivity = true;
        exceptionsUnavailable = false;

        try
        {
            await LoadAsync();
        }
        finally
        {
            isRefreshing = false;
        }
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

    /// <summary>
    /// Transfers raised on one day. "all" lifts the endpoint's default filter, which would
    /// otherwise count only the ones still awaiting a decision.
    /// </summary>
    private async Task<int?> CountTransfersRaisedAsync(string status, DateTime date)
    {
        var response = await TransferService.GetPendingTransfersAsync(
            status, pageSize: 1, fromDate: date, toDate: date);

        return response?.TotalCount;
    }

    private async Task LoadTransferDayStatsAsync()
    {
        try
        {
            var today = DateTime.Today;
            var todayTask = CountTransfersRaisedAsync("all", today);
            var yesterdayTask = CountTransfersRaisedAsync("all", today.AddDays(-1));
            var postedTask = CountTransfersRaisedAsync(PendingTransferStatuses.Posted, today);
            await Task.WhenAll(todayTask, yesterdayTask, postedTask);

            todayTransferCount = await todayTask;
            todayTransfersPosted = await postedTask;
            transferTrend = BuildTrend(todayTransferCount, await yesterdayTask);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard could not read today's transfer figures.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Counts sales orders without drawing any. The smallest page still carries an authoritative
    /// TotalCount, and a source-filtered read is answered from the local tables alone — which is
    /// what keeps the mobile figures off SAP entirely.
    /// </summary>
    private async Task<int?> CountSalesOrdersAsync(
        DateTime date,
        SalesOrderSource? source = null,
        SalesOrderStatus? status = null)
    {
        var response = await SalesOrderService.GetSalesOrdersAsync(
            page: 1,
            pageSize: 1,
            status: status,
            fromDate: date,
            toDate: date,
            source: source);

        return response?.TotalCount;
    }

    private async Task LoadMobileOrderStatsAsync()
    {
        try
        {
            var today = DateTime.Today;
            var todayTask = CountSalesOrdersAsync(today, SalesOrderSource.Mobile);
            var yesterdayTask = CountSalesOrdersAsync(today.AddDays(-1), SalesOrderSource.Mobile);
            // Mobile lines arrive unpriced, so a mobile order sits at Pending until someone has
            // priced and approved it. That queue is the point of the card, not the raw count.
            var toReviewTask = CountSalesOrdersAsync(today, SalesOrderSource.Mobile, SalesOrderStatus.Pending);
            await Task.WhenAll(todayTask, yesterdayTask, toReviewTask);

            todayMobileOrderCount = await todayTask;
            mobileOrdersToReview = await toReviewTask;
            mobileOrderTrend = BuildTrend(todayMobileOrderCount, await yesterdayTask);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard could not read today's mobile order figures.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadSalesOrderStatsAsync()
    {
        try
        {
            var today = DateTime.Today;
            var todayTask = CountSalesOrdersAsync(today);
            var yesterdayTask = CountSalesOrdersAsync(today.AddDays(-1));
            // Pending is a status SAP has no equivalent for, so this one counts exactly the orders
            // raised in the app today that have not been approved onward into SAP.
            var toApproveTask = CountSalesOrdersAsync(today, status: SalesOrderStatus.Pending);
            await Task.WhenAll(todayTask, yesterdayTask, toApproveTask);

            todaySalesOrderCount = await todayTask;
            salesOrdersToApprove = await toApproveTask;
            salesOrderTrend = BuildTrend(todaySalesOrderCount, await yesterdayTask);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard could not read today's sales order figures.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
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

    /// <summary>
    /// A comparison against yesterday, drawn only when both days actually read. A failed read is
    /// not a day with no documents, and must not be printed as a fall to zero.
    /// </summary>
    private static (string? Text, int Direction) BuildTrend(int? today, int? yesterday) =>
        today is { } count && yesterday is { } yesterdayCount
            ? DashboardFigures.BuildTrend(count, yesterdayCount)
            : (null, 0);

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

    /// <summary>
    /// The banner's sentence. The word itself comes from
    /// <see cref="SystemHealthSummary"/>; this only puts it in one.
    /// </summary>
    private string HealthHeadline => healthStatus is null
        ? "Reading system health"
        : $"System {HealthValue.ToLowerInvariant()}";

    /// <summary>
    /// Which family the banner paints in. Nothing read yet is neither good news
    /// nor bad, and must not be dressed as either.
    /// </summary>
    private string HealthStateClass => healthStatus is null
        ? "is-unknown"
        : Health.IsHealthy ? "is-ok" : "is-bad";

    /// <summary>
    /// The retry rail reads as a condition rather than a count, because the
    /// count beside it — Stalled — is the one worth a number.
    /// </summary>
    private string RetriesRail => retryOverdueCount switch
    {
        null => "—",
        0 => "Normal",
        _ => $"{retryOverdueCount:N0} overdue"
    };

    /// <summary>
    /// The lock-out line under the accounts card, which leads with the standing
    /// 2FA exposure instead. A lock-out is today's incident; the design foots
    /// the card with it for that reason.
    /// </summary>
    private string? LockoutNote => lockedUsers switch
    {
        null => null,
        0 => "0 locked out today",
        1 => "1 locked out today",
        _ => $"{lockedUsers:N0} locked out today"
    };

    /// <summary>
    /// How much of today's mobile intake is still unpriced. A day with nothing left says so; a day
    /// with nothing raised leaves the line off, because "0 to review" beside a zero count is the
    /// same fact twice.
    /// </summary>
    private string? MobileReviewNote => mobileOrdersToReview switch
    {
        null => null,
        0 when todayMobileOrderCount is > 0 => "All reviewed",
        0 => null,
        var toReview => $"{toReview:N0} to review"
    };

    private string? SalesOrderApprovalNote => salesOrdersToApprove switch
    {
        null => null,
        0 when todaySalesOrderCount is > 0 => "All approved",
        0 => null,
        var toApprove => $"{toApprove:N0} to approve"
    };

    /// <summary>
    /// How many of today's transfers made it into SAP. Posted can read higher than raised — a
    /// transfer raised yesterday and posted today counts in one and not the other — so the line
    /// says "all" rather than a figure that would look like an error.
    /// </summary>
    private string? TransferPostedNote => (todayTransferCount, todayTransfersPosted) switch
    {
        (null, _) or (_, null) => null,
        (0, _) => null,
        var (raised, posted) when posted >= raised => "All posted to SAP",
        var (_, posted) => $"{posted:N0} posted to SAP"
    };

    /// <summary>
    /// What the people who signed in today actually did. The design names them
    /// instead, but no read returns the day's roll of users — only this count.
    /// </summary>
    private string? ActionsNote => totalActions switch
    {
        null => null,
        _ when failedActions is > 0 => $"{totalActions:N0} actions, {failedActions:N0} failed",
        _ => $"{totalActions:N0} actions"
    };

    /// <summary>
    /// The families present in the clusters on screen, in the order the
    /// exception centre ranked them. Taken from the data rather than a fixed
    /// list, so the filter cannot offer a choice that empties the table.
    /// </summary>
    private IReadOnlyList<string> Families => clusters is null
        ? []
        : clusters
            .Select(cluster => cluster.Family)
            .Where(family => !string.IsNullOrWhiteSpace(family))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<ExceptionCenterClusterModel>? VisibleClusters => familyFilter is null
        ? clusters
        : clusters?
            .Where(cluster => string.Equals(cluster.Family, familyFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// How many failures the panel is reporting — the sum of the clusters
    /// shown, not the number of rows, because one row can stand for twenty
    /// failures.
    /// </summary>
    private int? ClusterTotal => VisibleClusters is null or { Count: 0 }
        ? null
        : VisibleClusters.Sum(cluster => cluster.Count);

    private void SetFamily(string? family) => familyFilter = family;

    /// <summary>
    /// ARIA takes the two lowercase strings, not .NET's "True"/"False".
    /// </summary>
    private static string Pressed(bool isOn) => isOn ? "true" : "false";

    /// <summary>
    /// The card's grade. Accent is reserved for a card that actually wants the
    /// administrator: a settled one falls back to the plain surface, so the
    /// wash keeps meaning something. Colour never carries the state alone —
    /// each card states its condition in words beneath the figure.
    /// </summary>
    private static string? ToneClass(StatTone tone) => tone switch
    {
        StatTone.Critical => "is-risk",
        StatTone.Ok => "is-clear",
        StatTone.Warn => null,
        _ => "is-clear"
    };

    private static string TrendIcon(int direction) => direction switch
    {
        > 0 => "arrow-up",
        < 0 => "arrow-down",
        _ => "flat"
    };

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

    private StatTone SecurityTone => lockedUsers switch
    {
        null => StatTone.Neutral,
        > 0 => StatTone.Warn,
        _ when usersWithoutTwoFactor > 0 => StatTone.Warn,
        _ => StatTone.Ok
    };

    /// <summary>Coarse age, because the card has room for two words.</summary>
    private static string DescribeAge(TimeSpan age) => age switch
    {
        { TotalMinutes: < 60 } => $"{Math.Max(1, (int)age.TotalMinutes)}m",
        { TotalHours: < 24 } => $"{(int)age.TotalHours}h",
        _ => $"{(int)age.TotalDays}d"
    };
}
