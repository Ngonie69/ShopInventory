using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.Reports.Commands.BackfillFiscalTransactionLog;
using ShopInventory.Web.Features.Reports.Queries.GetFiscalTransactionLog;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class FiscalTransactionLog : IDisposable
{
    private const int TransactionsPerPage = 25;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] private ILogger<FiscalTransactionLog> Logger { get; set; } = default!;

    private GetFiscalTransactionLogResult reportResult = new();
    private FiscalTransactionLogItemModel? selectedTransaction;
    private bool isDetailsModalOpen;
    private bool isLoading = true;
    private bool hasInitialized;
    private bool hasLoggedView;
    private bool awaitingAuthentication;
    private bool canBackfill;
    private bool isBackfilling;
    private string? errorMessage;
    private string? successMessage;
    private string searchTerm = string.Empty;
    private string selectedStatus = "All";
    private string selectedDocumentType = "All";
    private string selectedSourceSystem = "All";
    private DateTime? fromDate = DateTime.Today.AddDays(-30);
    private DateTime? toDate = DateTime.Today;
    private int currentPage = 1;

    private string PageSummary => reportResult.TotalCount == 0
        ? "No records returned"
        : $"{reportResult.TotalCount} total records across {Math.Max(1, currentPage)} page(s)";

    private string LatestActivityText => reportResult.Summary.LatestTransactionAtUtc.HasValue
        ? $"Latest activity {FormatTimestamp(reportResult.Summary.LatestTransactionAtUtc.Value)}"
        : "No activity yet";

    protected override void OnInitialized()
    {
        AuthStateProvider.AuthenticationStateChanged += HandleAuthenticationStateChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
        {
            return;
        }

        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        if (!(authState.User.Identity?.IsAuthenticated ?? false))
        {
            awaitingAuthentication = true;
            isLoading = false;
            return;
        }

        hasInitialized = true;
        await LoadReportAsync();
        StateHasChanged();
    }

    private async Task LoadReportAsync()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            var authState = await AuthStateProvider.GetAuthenticationStateAsync();
            if (!(authState.User.Identity?.IsAuthenticated ?? false))
            {
                awaitingAuthentication = true;
                reportResult = new GetFiscalTransactionLogResult();
                selectedTransaction = null;
                isDetailsModalOpen = false;
                return;
            }

            awaitingAuthentication = false;
            canBackfill = authState.User.IsInRole("Admin") || authState.User.IsInRole("Manager");
            var selectedTransactionId = selectedTransaction?.Id;
            var result = await Mediator.Send(new GetFiscalTransactionLogQuery(
                fromDate,
                toDate,
                string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm.Trim(),
                NormalizeFilter(selectedStatus),
                NormalizeFilter(selectedDocumentType),
                NormalizeFilter(selectedSourceSystem),
                currentPage,
                TransactionsPerPage));

            result.SwitchFirst(
                value =>
                {
                    reportResult = value;
                    selectedTransaction = value.Transactions.FirstOrDefault(transaction => transaction.Id == selectedTransactionId);
                    isDetailsModalOpen = isDetailsModalOpen && selectedTransaction is not null;
                },
                error =>
                {
                    reportResult = new GetFiscalTransactionLogResult();
                    selectedTransaction = null;
                    isDetailsModalOpen = false;
                    errorMessage = error.Description;
                });

            if (!result.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
                await AuditService.LogAsync(AuditActions.ViewReports, "Report", "FiscalTransactionLog");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load fiscal transaction log page");
            reportResult = new GetFiscalTransactionLogResult();
            selectedTransaction = null;
            isDetailsModalOpen = false;
            errorMessage = "Failed to load fiscal transaction log.";
        }
        finally
        {
            isLoading = false;
        }
    }

    private async void HandleAuthenticationStateChanged(Task<AuthenticationState> authenticationStateTask)
    {
        try
        {
            var authState = await authenticationStateTask;
            if (!(authState.User.Identity?.IsAuthenticated ?? false))
            {
                return;
            }

            await InvokeAsync(async () =>
            {
                if (!hasInitialized)
                {
                    hasInitialized = true;
                }

                if (awaitingAuthentication || (!hasLoggedView && string.IsNullOrWhiteSpace(errorMessage)))
                {
                    await LoadReportAsync();
                    StateHasChanged();
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Authentication state change handler failed for fiscal transaction log page");
        }
    }

    private async Task ApplyFiltersAsync()
    {
        currentPage = 1;
        await LoadReportAsync();
    }

    private async Task ClearFiltersAsync()
    {
        searchTerm = string.Empty;
        selectedStatus = "All";
        selectedDocumentType = "All";
        selectedSourceSystem = "All";
        fromDate = DateTime.Today.AddDays(-30);
        toDate = DateTime.Today;
        currentPage = 1;
        await LoadReportAsync();
    }

    private async Task ReloadAsync() => await LoadReportAsync();

    private async Task BackfillCurrentRangeAsync()
    {
        if (isBackfilling)
        {
            return;
        }

        isBackfilling = true;
        errorMessage = null;

        try
        {
            var result = await Mediator.Send(new BackfillFiscalTransactionLogCommand(fromDate, toDate));
            result.SwitchFirst(
                value => successMessage = BuildBackfillSummary(value),
                error => errorMessage = error.Description);

            if (!result.IsError)
            {
                currentPage = 1;
                await LoadReportAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to run fiscal transaction backfill from the report page");
            errorMessage = "Failed to backfill fiscalised invoices.";
        }
        finally
        {
            isBackfilling = false;
        }
    }

    private async Task PreviousPageAsync()
    {
        if (currentPage <= 1)
        {
            return;
        }

        currentPage--;
        await LoadReportAsync();
    }

    private async Task NextPageAsync()
    {
        if (!reportResult.HasMore)
        {
            return;
        }

        currentPage++;
        await LoadReportAsync();
    }

    private void OpenTransactionDetails(FiscalTransactionLogItemModel transaction)
    {
        selectedTransaction = transaction;
        isDetailsModalOpen = true;
    }

    private void CloseTransactionDetails()
        => isDetailsModalOpen = false;

    private string GetRowClass(FiscalTransactionLogItemModel transaction)
        => selectedTransaction?.Id == transaction.Id ? "ftr-row--selected" : string.Empty;

    private string GetStatusBadgeClass(string? status)
        => (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "success" => "ftr-status-badge ftr-status-badge--success",
            "fiscalised" => "ftr-status-badge ftr-status-badge--info",
            "not fiscalised" => "ftr-status-badge ftr-status-badge--warning",
            "failed" => "ftr-status-badge ftr-status-badge--danger",
            _ => "ftr-status-badge ftr-status-badge--muted"
        };

    private static string GetSourceBadgeClass(string? sourceSystem)
        => (sourceSystem ?? string.Empty).Trim() switch
        {
            "InvoiceFiscalisation" => "ftr-source-badge ftr-source-badge--queue",
            "RevmaxEndpoint" => "ftr-source-badge ftr-source-badge--endpoint",
            "InvoiceFiscalisationBackfill" => "ftr-source-badge ftr-source-badge--backfill",
            _ => "ftr-source-badge ftr-source-badge--muted"
        };

    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "All", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();

    private static string DisplayCustomer(FiscalTransactionLogItemModel transaction)
        => string.IsNullOrWhiteSpace(transaction.CardName) ? "Walk-in / not captured" : transaction.CardName;

    private static string DisplayCardCode(FiscalTransactionLogItemModel transaction)
        => string.IsNullOrWhiteSpace(transaction.CardCode) ? "No card code" : transaction.CardCode;

    private static string DisplayVerification(FiscalTransactionLogItemModel transaction)
        => string.IsNullOrWhiteSpace(transaction.VerificationCode) ? "Not captured" : transaction.VerificationCode;

    private static string DisplayReceiptGlobalNo(FiscalTransactionLogItemModel transaction)
        => transaction.ReceiptGlobalNo.HasValue && transaction.ReceiptGlobalNo.Value > 0
            ? transaction.ReceiptGlobalNo.Value.ToString()
            : "Not captured";

    private static string DisplayOperator(FiscalTransactionLogItemModel transaction)
        => string.IsNullOrWhiteSpace(transaction.CreatedByUsername)
            ? (string.IsNullOrWhiteSpace(transaction.CreatedByUserId) ? "Unknown operator" : transaction.CreatedByUserId)
            : transaction.CreatedByUsername;

    private static string DisplayOriginalInvoice(FiscalTransactionLogItemModel transaction)
        => string.IsNullOrWhiteSpace(transaction.OriginalInvoiceNumber) ? "Not applicable" : transaction.OriginalInvoiceNumber;

    private static string DisplayDeviceSerial(FiscalTransactionLogItemModel transaction)
        => string.IsNullOrWhiteSpace(transaction.DeviceSerialNumber) ? "Not captured" : transaction.DeviceSerialNumber;

    private static string DisplayDeviceId(FiscalTransactionLogItemModel transaction)
        => string.IsNullOrWhiteSpace(transaction.DeviceId) ? "Not captured" : transaction.DeviceId;

    private static string DisplayFiscalDay(FiscalTransactionLogItemModel transaction)
        => string.IsNullOrWhiteSpace(transaction.FiscalDay) ? "Not captured" : transaction.FiscalDay;

    private static string DisplayMessage(FiscalTransactionLogItemModel transaction)
        => string.IsNullOrWhiteSpace(transaction.Message) ? "No operator message captured." : transaction.Message;

    private static string DisplaySourceSystem(string? sourceSystem)
        => (sourceSystem ?? string.Empty).Trim() switch
        {
            "InvoiceFiscalisation" => "API Queue",
            "RevmaxEndpoint" => "REVMax Endpoint",
            "InvoiceFiscalisationBackfill" => "Backfill",
            _ => string.IsNullOrWhiteSpace(sourceSystem) ? "Unknown source" : sourceSystem
        };

    private static string BuildBackfillSummary(BackfillFiscalTransactionLogResult result)
    {
        var window = $"{IAuditService.ToCAT(result.FromUtc):yyyy-MM-dd} to {IAuditService.ToCAT(result.ToUtc):yyyy-MM-dd}";
        var scope = result.ScannedInvoiceCount < result.AvailableInvoiceCount
            ? $"Scanned {result.ScannedInvoiceCount} of {result.AvailableInvoiceCount} available invoices."
            : $"Scanned {result.ScannedInvoiceCount} invoice(s).";

        return $"Backfill completed for {window}. Synced {result.TransactionsSyncedCount} fiscalised invoice(s), skipped {result.AlreadyTrackedCount} already tracked, found {result.NotFiscalisedCount} not fiscalised, with {result.LookupFailedCount} lookup failure(s) and {result.SyncFailedCount} sync failure(s). {scope}";
    }

    private static string FormatAmount(decimal amount, string? currency)
        => string.IsNullOrWhiteSpace(currency) ? amount.ToString("N2") : $"{currency} {amount:N2}";

    private static string FormatTimestamp(DateTime timestampUtc)
        => IAuditService.ToCAT(timestampUtc).ToString("yyyy-MM-dd HH:mm");

    public void Dispose()
    {
        AuthStateProvider.AuthenticationStateChanged -= HandleAuthenticationStateChanged;
    }
}