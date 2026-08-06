using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Common;
using ShopInventory.Web.Components.Dashboard;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.Reports.Queries.GetFiscalTransactionLog;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The cashier's dashboard at /cashier-dashboard.
///
/// Every figure is nullable until its read lands, so the page shows "—" rather
/// than a zero that is about to jump — a zero on the fiscalisation cards would
/// read as "nothing failed", which is not the same as not knowing yet.
///
/// Each read is caught separately so one dead service cannot blank the page,
/// and each catch logs.
/// </summary>
public partial class CashierDashboard
{
    [Inject] private IInvoiceService InvoiceService { get; set; } = default!;
    [Inject] private IPaymentService PaymentService { get; set; } = default!;
    [Inject] private ICreditNoteService CreditNoteService { get; set; } = default!;
    [Inject] private ISalesOrderService SalesOrderService { get; set; } = default!;
    [Inject] private ISender Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private ILogger<CashierDashboard> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    /// <summary>Rows shown in each of the two activity tables.</summary>
    private const int RecentRowsRead = 10;

    /// <summary>When the figures on screen were read. Null until the first read lands.</summary>
    private DateTime? loadedAt;

    private int? todayInvoiceCount;
    private int? todayPaymentCount;
    private int? todayCreditNoteCount;
    private decimal invoiceTotal;
    private decimal paymentTotal;
    private (string? Text, int Direction) invoiceTrend;
    private (string? Text, int Direction) paymentTrend;
    private (string? Text, int Direction) creditNoteTrend;

    private int? awaitingInvoiceCount;

    private int? fiscalFailedCount;
    private int? notFiscalisedCount;

    private List<InvoiceDto>? recentInvoices;
    private List<IncomingPaymentDto>? recentPayments;
    private bool isLoadingInvoices = true;
    private bool isLoadingPayments = true;

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
        await AuditService.LogAsync(AuditActions.ViewDashboard, "CashierDashboard", null);
    }

    private async Task LoadAsync()
    {
        await Task.WhenAll(
            LoadInvoiceStatsAsync(),
            LoadPaymentStatsAsync(),
            LoadCreditNoteCountAsync(),
            LoadAwaitingInvoiceCountAsync(),
            LoadFiscalCountsAsync(),
            LoadRecentInvoicesAsync(),
            LoadRecentPaymentsAsync());

        // Stamped once every read has landed, so the header's time describes the
        // whole page rather than whichever call returned first.
        loadedAt = DateTime.Now;
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
            Logger.LogWarning(ex, "Cashier dashboard could not read today's invoice figures.");
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
            Logger.LogWarning(ex, "Cashier dashboard could not read today's payment figures.");
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
            // Both days are counted so this card carries the same comparison as
            // the two beside it. Only the totals are drawn, so each asks for the
            // smallest page that still returns an authoritative TotalCount.
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
            Logger.LogWarning(ex, "Cashier dashboard could not count today's credit notes.");
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

    private async Task LoadAwaitingInvoiceCountAsync()
    {
        try
        {
            // Approved is the status before invoicing: SalesOrderStatus.Invoiced
            // is an alias of Fulfilled, so an approved order is one that has been
            // agreed and not yet turned into an invoice. Not date-scoped — an
            // order waiting since last week is the one worth chasing.
            var response = await SalesOrderService.GetSalesOrdersAsync(
                page: 1, pageSize: 1, status: SalesOrderStatus.Approved);

            awaitingInvoiceCount = response?.TotalCount;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Cashier dashboard could not count sales orders awaiting an invoice.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadFiscalCountsAsync()
    {
        try
        {
            // The handler computes its summary over the whole filtered set before
            // it pages, so a page size of one still returns accurate counts for
            // the day. Only the summary is read here.
            var today = DateTime.Today;
            var result = await Mediator.Send(new GetFiscalTransactionLogQuery(
                FromDate: today,
                ToDate: today,
                Search: null,
                Status: null,
                DocumentType: null,
                SourceSystem: null,
                ClientTransactionPrefix: null,
                Page: 1,
                PageSize: 1));

            if (result.IsError)
            {
                Logger.LogWarning(
                    "Cashier dashboard could not read the fiscal transaction summary: {Error}",
                    result.FirstError.Description);
                return;
            }

            fiscalFailedCount = result.Value.Summary.FailedCount;
            notFiscalisedCount = result.Value.Summary.NotFiscalisedCount;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Cashier dashboard could not read the fiscal transaction summary.");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadRecentInvoicesAsync()
    {
        try
        {
            var response = await InvoiceService.GetInvoicesAsync(1, RecentRowsRead);
            if (response?.Invoices != null)
            {
                recentInvoices = response.Invoices;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Cashier dashboard could not read recent invoices.");
        }
        finally
        {
            isLoadingInvoices = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadRecentPaymentsAsync()
    {
        try
        {
            var response = await PaymentService.GetPaymentsAsync(1, RecentRowsRead);
            if (response?.Payments != null)
            {
                recentPayments = response.Payments;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Cashier dashboard could not read recent payments.");
        }
        finally
        {
            isLoadingPayments = false;
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

    private StatTone FiscalTone => fiscalFailedCount switch
    {
        null => StatTone.Neutral,
        0 => StatTone.Ok,
        _ => StatTone.Critical
    };

    private string? FiscalNote => fiscalFailedCount switch
    {
        null => null,
        0 => "Nothing failed today",
        _ => "Rejected by the fiscal device"
    };

    /// <summary>
    /// Not-fiscalised is weaker news than failed: an invoice can be waiting its
    /// turn rather than rejected. It only earns a warning once there are some.
    /// </summary>
    private StatTone NotFiscalisedTone => notFiscalisedCount switch
    {
        null => StatTone.Neutral,
        0 => StatTone.Ok,
        _ => StatTone.Warn
    };

    private string? NotFiscalisedNote => notFiscalisedCount switch
    {
        null => null,
        0 => "All receipted",
        _ => "Waiting on the device"
    };
}
