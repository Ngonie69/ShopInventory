using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Common;
using ShopInventory.Web.Components.Dashboard;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The manager's dashboard at /manager-dashboard.
///
/// Every figure is nullable until its read lands, so the page shows "—" rather
/// than a zero that is about to jump. Each read is caught separately so one
/// dead service cannot blank the page, and each catch logs.
/// </summary>
public partial class ManagerDashboard
{
    [Inject] private IPurchaseOrderService PurchaseOrderService { get; set; } = default!;
    [Inject] private IPurchaseRequestService PurchaseRequestService { get; set; } = default!;
    [Inject] private IGoodsReceiptPurchaseOrderService GoodsReceiptService { get; set; } = default!;
    [Inject] private IInventoryTransferService TransferService { get; set; } = default!;
    [Inject] private IInvoiceService InvoiceService { get; set; } = default!;
    [Inject] private IPaymentService PaymentService { get; set; } = default!;
    [Inject] private ICreditNoteService CreditNoteService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private ILogger<ManagerDashboard> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    /// <summary>
    /// The window the purchasing figures cover. Those endpoints carry no status
    /// filter, so an all-time total would count every document ever raised; a
    /// week is the span a manager is actually working in.
    /// </summary>
    private const int WindowDays = 7;

    /// <summary>Rows drawn in each panel.</summary>
    private const int RowsShown = 6;

    private DateTime? loadedAt;

    private int? pendingOrderCount;
    private int? approvedOrderCount;
    private int? recentRequestCount;
    private int? recentReceiptCount;
    private int? pendingTransferCount;

    private int? activeUsers;
    private int? totalActions;
    private int? failedActions;

    private int? todayInvoiceCount;
    private int? todayPaymentCount;
    private int? todayCreditNoteCount;
    private decimal invoiceTotal;
    private decimal paymentTotal;
    private (string? Text, int Direction) invoiceTrend;
    private (string? Text, int Direction) paymentTrend;
    private (string? Text, int Direction) creditNoteTrend;

    private List<PurchaseOrderDto>? pendingOrders;
    private List<AuditLog>? recentActivity;
    private bool isLoadingApprovals = true;
    private bool isLoadingActivity = true;

    private string currentUsername = "there";
    private bool _initialized;

    protected override async Task OnParametersSetAsync()
    {
        if (AuthTask is null || _initialized) return;

        var authState = await AuthTask;
        var user = authState.User;

        if (user.Identity?.IsAuthenticated != true) return;

        _initialized = true;
        currentUsername = user.Identity?.Name ?? currentUsername;

        await LoadAsync();
        await AuditService.LogAsync(AuditActions.ViewDashboard, "ManagerDashboard", null);
    }

    private async Task LoadAsync()
    {
        await Task.WhenAll(
            LoadPurchaseOrderCountsAsync(),
            LoadPendingOrdersAsync(),
            LoadRecentRequestCountAsync(),
            LoadRecentReceiptCountAsync(),
            LoadPendingTransferCountAsync(),
            LoadActivityStatsAsync(),
            LoadRecentActivityAsync(),
            LoadInvoiceStatsAsync(),
            LoadPaymentStatsAsync(),
            LoadCreditNoteCountAsync());

        // Stamped once every read has landed, so the header's time describes the
        // whole page rather than whichever call returned first.
        loadedAt = DateTime.Now;
    }

    private async Task LoadPurchaseOrderCountsAsync()
    {
        pendingOrderCount = await CountOrdersAsync(PurchaseOrderStatus.Pending);
        approvedOrderCount = await CountOrdersAsync(PurchaseOrderStatus.Approved);
        await InvokeAsync(StateHasChanged);
    }

    private async Task<int?> CountOrdersAsync(PurchaseOrderStatus status)
    {
        try
        {
            // Only the total is drawn, so ask for the smallest page that still
            // carries an authoritative TotalCount.
            var response = await PurchaseOrderService.GetPurchaseOrdersAsync(
                page: 1, pageSize: 1, status: status);

            return response?.TotalCount;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Manager dashboard could not count {Status} purchase orders.", status);
            return null;
        }
    }

    private async Task LoadPendingOrdersAsync()
    {
        try
        {
            var response = await PurchaseOrderService.GetPurchaseOrdersAsync(
                page: 1, pageSize: RowsShown, status: PurchaseOrderStatus.Pending);

            // Oldest first: the top of the queue should be what has waited longest.
            pendingOrders = response?.Orders
                .OrderBy(order => order.OrderDate)
                .Take(RowsShown)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Manager dashboard could not read the purchase order approval queue.");
        }
        finally
        {
            isLoadingApprovals = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadRecentRequestCountAsync()
    {
        try
        {
            // Dated on purpose. Without dates the handler counts every purchase
            // request in SAP regardless of status, which is a number that only
            // grows. With them it counts the window, which is the question.
            var response = await PurchaseRequestService.GetPurchaseRequestsAsync(
                page: 1, pageSize: 1, fromDate: WindowStart, toDate: DateTime.Today);

            recentRequestCount = response?.TotalCount;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Manager dashboard could not count recent purchase requests.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadRecentReceiptCountAsync()
    {
        try
        {
            var response = await GoodsReceiptService.GetGoodsReceiptPurchaseOrdersAsync(
                page: 1, pageSize: 1, fromDate: WindowStart, toDate: DateTime.Today);

            recentReceiptCount = response?.TotalCount;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Manager dashboard could not count recent goods receipts.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadPendingTransferCountAsync()
    {
        try
        {
            var response = await TransferService.GetPendingTransfersAsync(
                PendingTransferStatuses.AwaitingApproval, pageSize: 1);

            pendingTransferCount = response?.TotalCount;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Manager dashboard could not count transfers awaiting approval.");
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
            Logger.LogWarning(ex, "Manager dashboard could not read today's activity statistics.");
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
            var page = await AuditService.GetLogPageAsync(page: 1, pageSize: RowsShown);
            recentActivity = page.Items;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Manager dashboard could not read the recent activity log.");
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
            Logger.LogWarning(ex, "Manager dashboard could not read today's invoice figures.");
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
            Logger.LogWarning(ex, "Manager dashboard could not read today's payment figures.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadCreditNoteCountAsync()
    {
        try
        {
            var today = DateTime.Today;
            var todayTask = CountCreditNotesAsync(today);
            var yesterdayTask = CountCreditNotesAsync(today.AddDays(-1));
            await Task.WhenAll(todayTask, yesterdayTask);

            var count = await todayTask;
            var yesterdayCount = await yesterdayTask;

            todayCreditNoteCount = count;
            if (count is not null && yesterdayCount is not null)
            {
                creditNoteTrend = DashboardFigures.BuildTrend(count.Value, yesterdayCount.Value);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Manager dashboard could not count today's credit notes.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<int?> CountCreditNotesAsync(DateTime date)
    {
        var response = await CreditNoteService.GetCreditNotesAsync(
            page: 1, pageSize: 1, fromDate: date, toDate: date);

        return response?.TotalCount;
    }

    // ── Presentation ────────────────────────────────────────────────────────

    private static DateTime WindowStart => DateTime.Today.AddDays(-WindowDays);

    private static string GreetingText => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening"
    };

    /// <summary>A figure that has not landed yet reads as a dash, not a zero.</summary>
    private static string Figure(int? value) => value?.ToString("N0") ?? "—";

    private static string FormatMoney(decimal value) => $"USD {value:N2}";

    private StatTone PendingOrderTone => pendingOrderCount switch
    {
        null => StatTone.Neutral,
        0 => StatTone.Ok,
        _ => StatTone.Warn
    };

    private string? PendingOrderNote => pendingOrderCount switch
    {
        null => null,
        0 => "Queue is clear",
        _ => "Waiting on you"
    };

    private StatTone ActivityTone =>
        failedActions is null or 0 ? StatTone.Neutral : StatTone.Warn;

    private string? ActivityNote =>
        failedActions is null or 0 ? null : $"{failedActions:N0} failed";

    /// <summary>
    /// The first name to greet by, taken from the username. An address local
    /// part carries the name in one of a few shapes — first.last, first_last,
    /// or just the name — so all three reduce to the same greeting.
    /// </summary>
    private string GreetingName
    {
        get
        {
            var localName = currentUsername.Split('@')[0];
            var firstName = localName
                .Replace('.', ' ')
                .Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(firstName)) return "there";
            return firstName.Length == 1
                ? firstName.ToUpperInvariant()
                : char.ToUpperInvariant(firstName[0]) + firstName[1..];
        }
    }
}
