using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Components;
using QRCoder;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.Reports.Queries.GetFiscalTransactionLog;
using ShopInventory.Web.Features.Revmax;
using ShopInventory.Web.Features.Revmax.Commands.FiscalizeCrossDeviceCreditNote;
using ShopInventory.Web.Features.Revmax.Queries.GetCrossDeviceCreditNoteHistory;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class RevmaxCrossDeviceCreditNoteHistory : ComponentBase
{
    private const int TransactionsPerPage = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private ILogger<RevmaxCrossDeviceCreditNoteHistory> Logger { get; set; } = default!;

    private GetFiscalTransactionLogResult historyResult = new();
    private FiscalTransactionLogItemModel? selectedTransaction;
    private RevmaxTransactExtApiRequest? selectedRequest;
    private RevmaxTransactExtResponse? selectedResponse;
    private RevmaxInvoiceData? selectedReceiptData;
    private string? selectedQrCodeImageSrc;
    private string? selectedRawRequestJson;
    private string? selectedRawResponseJson;
    private string? selectedDisplayMessage;
    private string? selectedFailureSource;
    private string? selectedFailureEndpoint;
    private string? selectedFailureInvoiceNumber;
    private bool selectedHasFailurePayload;
    private bool isDetailsOpen;
    private bool isLoading = true;
    private bool hasLoggedView;
    private string? errorMessage;
    private string searchTerm = string.Empty;
    private string selectedStatus = "All";
    private DateTime? toDate = IAuditService.ToCAT(DateTime.UtcNow).Date;
    private DateTime? fromDate = IAuditService.ToCAT(DateTime.UtcNow).Date.AddDays(-30);
    private int currentPage = 1;

    private string PageSummary => historyResult.TotalCount == 0
        ? "No cross-device credit note transactions found"
        : $"{historyResult.TotalCount} transaction(s) returned";

    private string LatestActivityText => historyResult.Summary.LatestTransactionAtUtc.HasValue
        ? $"Latest {FormatTimestamp(historyResult.Summary.LatestTransactionAtUtc.Value)}"
        : "No activity yet";

    protected override async Task OnInitializedAsync()
        => await LoadHistoryAsync();

    private async Task LoadHistoryAsync()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            var selectedTransactionId = selectedTransaction?.Id;
            var result = await Mediator.Send(new GetCrossDeviceCreditNoteHistoryQuery(
                fromDate,
                toDate,
                string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm.Trim(),
                NormalizeFilter(selectedStatus),
                currentPage,
                TransactionsPerPage));

            result.SwitchFirst(
                value =>
                {
                    historyResult = value;
                    if (selectedTransactionId.HasValue)
                    {
                        var refreshed = value.Transactions.FirstOrDefault(transaction => transaction.Id == selectedTransactionId.Value);
                        if (refreshed is not null)
                        {
                            SelectTransaction(refreshed);
                        }
                        else
                        {
                            ClearSelection();
                        }
                    }
                },
                error =>
                {
                    historyResult = new GetFiscalTransactionLogResult();
                    ClearSelection();
                    errorMessage = error.Description;
                });

            if (!result.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
                await AuditService.LogAsync(AuditActions.ViewReports, "Report", "RevmaxCrossDeviceCreditNoteHistory");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load REVMax cross-device credit note history");
            historyResult = new GetFiscalTransactionLogResult();
            ClearSelection();
            errorMessage = "Failed to load cross-device credit note history.";
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task ApplyFiltersAsync()
    {
        currentPage = 1;
        await LoadHistoryAsync();
    }

    private async Task ClearFiltersAsync()
    {
        searchTerm = string.Empty;
        selectedStatus = "All";
        toDate = IAuditService.ToCAT(DateTime.UtcNow).Date;
        fromDate = toDate.Value.AddDays(-30);
        currentPage = 1;
        await LoadHistoryAsync();
    }

    private async Task ReloadAsync()
        => await LoadHistoryAsync();

    private async Task PreviousPageAsync()
    {
        if (currentPage <= 1)
        {
            return;
        }

        currentPage--;
        await LoadHistoryAsync();
    }

    private async Task NextPageAsync()
    {
        if (!historyResult.HasMore)
        {
            return;
        }

        currentPage++;
        await LoadHistoryAsync();
    }

    private void OpenTransactionDetails(FiscalTransactionLogItemModel transaction)
    {
        SelectTransaction(transaction);
        isDetailsOpen = true;
    }

    private void CloseTransactionDetails()
        => isDetailsOpen = false;

    private void SelectTransaction(FiscalTransactionLogItemModel transaction)
    {
        selectedTransaction = transaction;
        selectedRequest = ParseJson<RevmaxTransactExtApiRequest>(transaction.RawRequest);
        selectedResponse = RevmaxFailurePayloadReader.ParseResponse(transaction.RawResponse);
        selectedHasFailurePayload = RevmaxFailurePayloadReader.TryReadFailureDetails(
            transaction.RawResponse,
            out selectedFailureSource,
            out selectedFailureEndpoint,
            out selectedFailureInvoiceNumber,
            out var failureMessage);
        selectedReceiptData = ParseReceiptData(selectedResponse?.Data);
        selectedQrCodeImageSrc = BuildFiscalQrCodeImageSrc(selectedResponse?.QRcode ?? transaction.QRCode);
        selectedRawRequestJson = PrettyPrintJson(transaction.RawRequest);
        selectedRawResponseJson = PrettyPrintJson(transaction.RawResponse);
        selectedDisplayMessage = failureMessage
            ?? RevmaxFailurePayloadReader.CleanOperatorMessage(selectedResponse?.Message)
            ?? RevmaxFailurePayloadReader.CleanOperatorMessage(transaction.Message)
            ?? "Not captured";
    }

    private void ClearSelection()
    {
        selectedTransaction = null;
        selectedRequest = null;
        selectedResponse = null;
        selectedReceiptData = null;
        selectedQrCodeImageSrc = null;
        selectedRawRequestJson = null;
        selectedRawResponseJson = null;
        selectedDisplayMessage = null;
        selectedFailureSource = null;
        selectedFailureEndpoint = null;
        selectedFailureInvoiceNumber = null;
        selectedHasFailurePayload = false;
        isDetailsOpen = false;
    }

    private string GetStatusBadgeClass(string? status)
        => (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "success" => "cdnh-status cdnh-status--success",
            "fiscalised" => "cdnh-status cdnh-status--info",
            "failed" => "cdnh-status cdnh-status--danger",
            _ => "cdnh-status cdnh-status--muted"
        };

    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "All", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();

    private static string DisplayCustomer(FiscalTransactionLogItemModel transaction)
        => string.IsNullOrWhiteSpace(transaction.CardName) ? "Customer not captured" : transaction.CardName;

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

    private string GetSelectedReceiptGlobalNoText()
    {
        if (!string.IsNullOrWhiteSpace(selectedResponse?.ReceiptGlobalNo))
        {
            return selectedResponse.ReceiptGlobalNo;
        }

        if (selectedReceiptData?.ReceiptGlobalNo > 0)
        {
            return selectedReceiptData.ReceiptGlobalNo.ToString();
        }

        return selectedTransaction is null ? "Not captured" : DisplayReceiptGlobalNo(selectedTransaction);
    }

    private string GetSelectedFiscalDayText()
    {
        if (!string.IsNullOrWhiteSpace(selectedResponse?.FiscalDay))
        {
            return selectedResponse.FiscalDay;
        }

        if (!string.IsNullOrWhiteSpace(selectedResponse?.FiscalDayNo))
        {
            return selectedResponse.FiscalDayNo;
        }

        return string.IsNullOrWhiteSpace(selectedTransaction?.FiscalDay)
            ? "Not captured"
            : selectedTransaction.FiscalDay;
    }

    private string GetSelectedFailureDocumentText()
    {
        var fallbackDocNum = selectedTransaction is not null && selectedTransaction.DocNum > 0
            ? selectedTransaction.DocNum.ToString()
            : null;

        return FirstNonEmpty(selectedFailureInvoiceNumber, selectedRequest?.InvoiceNumber, fallbackDocNum) ?? "Not captured";
    }

    private string GetTaxesEmptyText()
        => selectedHasFailurePayload && selectedReceiptData is null
            ? "REVMax did not return any tax lines because this submission failed before a receipt was issued."
            : "No tax lines were returned.";

    private string GetPaymentsEmptyText()
        => selectedHasFailurePayload && selectedReceiptData is null
            ? "REVMax did not return any payment breakdown because this submission failed before a receipt was issued."
            : "No payment breakdown was returned.";

    private static string FormatAmount(decimal amount, string? currency)
        => string.IsNullOrWhiteSpace(currency) ? amount.ToString("N2") : $"{currency} {amount:N2}";

    private static string FormatTimestamp(DateTime timestampUtc)
        => IAuditService.ToCAT(timestampUtc).ToString("yyyy-MM-dd HH:mm");

    private static T? ParseJson<T>(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(rawJson, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static RevmaxInvoiceData? ParseReceiptData(object? data)
    {
        if (data is null)
        {
            return null;
        }

        if (data is RevmaxInvoiceData invoiceData)
        {
            return invoiceData;
        }

        if (data is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(element.GetString()))
            {
                return null;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    return element.Deserialize<RevmaxInvoiceData>(JsonOptions);
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static string? BuildFiscalQrCodeImageSrc(string? fiscalQrCode)
    {
        if (string.IsNullOrWhiteSpace(fiscalQrCode))
        {
            return null;
        }

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(fiscalQrCode.Trim(), QRCodeGenerator.ECCLevel.Q);
            var qrCode = new SvgQRCode(data);
            var svg = qrCode.GetGraphic(8);
            var bytes = Encoding.UTF8.GetBytes(svg);
            return $"data:image/svg+xml;base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    private static string? PrettyPrintJson(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return JsonSerializer.Serialize(document.RootElement, JsonOptions);
        }
        catch
        {
            return rawJson;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}