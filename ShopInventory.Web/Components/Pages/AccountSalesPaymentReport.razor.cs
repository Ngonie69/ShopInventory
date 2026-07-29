using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.Reports.Queries.GetAccountSalesPaymentReport;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class AccountSalesPaymentReport : IDisposable
{
    private const string DefaultAccountCodesText = "CIS006, COR006, COR007, MAC006, MAC009, COR008, COR011, VAN008-019";

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private ILogger<AccountSalesPaymentReport> Logger { get; set; } = default!;

    private GetAccountSalesPaymentReportResult reportResult = new();
    private CancellationTokenSource loadCts = new();
    private bool isDisposed;
    private bool hasLoggedView;
    private bool hasRequestedReport;
    private bool isExporting;
    private bool isLoading;
    private string? errorMessage;
    private DateTime? fromDate = DateTime.UtcNow.Date.AddDays(-30);
    private DateTime? toDate = DateTime.UtcNow.Date;
    private AccountSalesPaymentGrouping selectedGrouping = AccountSalesPaymentGrouping.Daily;
    private string accountCodesText = DefaultAccountCodesText;

    private List<AccountSalesPaymentPeriodResult> VisiblePeriods => reportResult.Periods
        .Where(period => period.InvoiceCount > 0 || period.PaymentCount > 0 || period.TotalQuantitySold > 0)
        .OrderByDescending(period => period.PeriodStartUtc)
        .ToList();

    private IEnumerable<string> DisplayedRequestedAccounts => reportResult.RequestedAccountCodes.Any()
        ? reportResult.RequestedAccountCodes
        : SplitAccountCodes(accountCodesText);

    private string SelectedGroupingLabel => selectedGrouping switch
    {
        AccountSalesPaymentGrouping.Daily => "Daily",
        AccountSalesPaymentGrouping.Weekly => "Weekly",
        AccountSalesPaymentGrouping.Monthly => "Monthly",
        _ => "Daily"
    };

    private string GroupingPeriodNoun => selectedGrouping switch
    {
        AccountSalesPaymentGrouping.Daily => "days",
        AccountSalesPaymentGrouping.Weekly => "weeks",
        AccountSalesPaymentGrouping.Monthly => "months",
        _ => "days"
    };

    private string GroupingPeriodSingular => selectedGrouping switch
    {
        AccountSalesPaymentGrouping.Daily => "day",
        AccountSalesPaymentGrouping.Weekly => "week",
        AccountSalesPaymentGrouping.Monthly => "month",
        _ => "day"
    };

    private string PrimaryActionLabel => hasRequestedReport ? "Refresh report" : "Load report";

    private bool CanExportReport => hasRequestedReport && reportResult.GeneratedAtUtc != default;

    private async Task LoadReportAsync()
    {
        var cancellationToken = BeginLoad();
        hasRequestedReport = true;
        isLoading = true;
        errorMessage = null;

        try
        {
            var result = await Mediator.Send(
                new GetAccountSalesPaymentReportQuery(
                    fromDate,
                    toDate,
                    selectedGrouping,
                    accountCodesText),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested || isDisposed)
            {
                return;
            }

            result.SwitchFirst(
                value => reportResult = value,
                error =>
                {
                    reportResult = new GetAccountSalesPaymentReportResult();
                    errorMessage = error.Description;
                });

            if (!result.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
                await AuditService.LogAsync(AuditActions.ViewReports, "Report", nameof(AccountSalesPaymentReport));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load account sales and incoming payment report page");
            reportResult = new GetAccountSalesPaymentReportResult();
            errorMessage = "Failed to load the account sales and incoming payment report.";
        }
        finally
        {
            if (!isDisposed)
            {
                isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task ApplyFiltersAsync() => await LoadReportAsync();

    private async Task ResetFiltersAsync()
    {
        fromDate = DateTime.UtcNow.Date.AddDays(-30);
        toDate = DateTime.UtcNow.Date;
        selectedGrouping = AccountSalesPaymentGrouping.Daily;
        accountCodesText = DefaultAccountCodesText;
        await LoadReportAsync();
    }

    private async Task SetGroupingAsync(AccountSalesPaymentGrouping grouping)
    {
        if (selectedGrouping == grouping)
        {
            return;
        }

        selectedGrouping = grouping;
        await LoadReportAsync();
    }

    private async Task ExportToExcelAsync()
    {
        if (!CanExportReport)
        {
            return;
        }

        isExporting = true;
        errorMessage = null;

        try
        {
            var bytes = ExportService.ExportAccountSalesPaymentReportToExcel(reportResult);
            var base64 = Convert.ToBase64String(bytes);
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"Account_Sales_And_Payments_{IAuditService.ToCAT(DateTime.UtcNow):yyyyMMdd_HHmm}.xlsx",
                base64);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to export account sales and incoming payment report to Excel");
            errorMessage = "Failed to export the account sales and incoming payment report to Excel.";
        }
        finally
        {
            isExporting = false;
        }
    }

    private string GetGroupingButtonClass(AccountSalesPaymentGrouping grouping) =>
        selectedGrouping == grouping
            ? "aspr-grouping-button aspr-grouping-button--active"
            : "aspr-grouping-button";

    private string FormatGeneratedAt() =>
        reportResult.GeneratedAtUtc == default
            ? string.Empty
            : IAuditService.ToCAT(reportResult.GeneratedAtUtc).ToString("dd MMM yyyy HH:mm 'CAT'");

    private static string FormatAmounts(decimal usd, decimal zig)
    {
        var parts = new List<string>();
        if (usd != 0 || zig == 0)
        {
            parts.Add($"USD {usd:N2}");
        }

        if (zig != 0 || usd == 0)
        {
            parts.Add($"ZiG {zig:N2}");
        }

        return string.Join(" • ", parts);
    }

    private static string FormatRates(decimal usdRate, decimal zigRate)
    {
        var parts = new List<string>();
        if (usdRate != 0 || zigRate == 0)
        {
            parts.Add($"USD {usdRate:N2}%");
        }

        if (zigRate != 0 || usdRate == 0)
        {
            parts.Add($"ZiG {zigRate:N2}%");
        }

        return string.Join(" • ", parts);
    }

    private static IEnumerable<AccountSalesPaymentAccountResult> GetOrderedAccounts(
        IEnumerable<AccountSalesPaymentAccountResult> accounts,
        bool includeZeroActivity = false)
    {
        var filteredAccounts = includeZeroActivity
            ? accounts
            : accounts.Where(account => account.InvoiceCount > 0 || account.PaymentCount > 0 || account.TotalQuantitySold > 0);

        return filteredAccounts
            .OrderByDescending(account => account.TotalSalesUsd + account.TotalSalesZig + account.IncomingPaymentsUsd + account.IncomingPaymentsZig)
            .ThenBy(account => account.CardCode, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> SplitAccountCodes(string value) =>
        value
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(accountCode => !string.IsNullOrWhiteSpace(accountCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private CancellationToken BeginLoad()
    {
        loadCts.Cancel();
        loadCts.Dispose();
        loadCts = new CancellationTokenSource();
        return loadCts.Token;
    }

    public void Dispose()
    {
        isDisposed = true;
        loadCts.Cancel();
        loadCts.Dispose();
    }
}