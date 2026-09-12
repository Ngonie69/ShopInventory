using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Service for accessing desktop integration data (invoices, transfers, reservations, queues)
/// </summary>
public interface IDesktopIntegrationService
{
    Task<DesktopCreditNoteResult> ContinueCreditNoteAsync(string reference, Guid id) => throw new NotSupportedException();
    Task<DesktopCreditForm> PrepareCreditNoteAsync(string reference) => throw new NotSupportedException();
    Task<List<DesktopCreditNoteResult>> GetCreditNotesAsync(string reference) => throw new NotSupportedException();
    Task<DesktopCreditNoteResult> CreateCreditNoteAsync(string reference, CreateDesktopCreditRequest request) => throw new NotSupportedException();
    Task<DesktopCreditNoteResult> ReconcileCreditNoteAsync(string reference, Guid id) => throw new NotSupportedException();
    // Invoice Queue
    Task<List<InvoiceQueueStatusDto>?> GetPendingQueueAsync(string? sourceSystem = null, int limit = 100);
    Task<List<InvoiceQueueStatusDto>?> GetInvoicesRequiringReviewAsync(int limit = 50);
    Task<InvoiceQueueStatsDto?> GetQueueStatsAsync();
    Task<InvoiceQueueStatusDto?> GetQueueStatusAsync(string externalReference);
    Task<bool> CancelQueuedInvoiceAsync(string externalReference);
    Task<bool> RetryQueuedInvoiceAsync(string externalReference);

    // Inventory Transfer Queue
    Task<List<InventoryTransferQueueStatusDto>?> GetPendingTransferQueueAsync(string? sourceSystem = null, int limit = 100);
    Task<List<InventoryTransferQueueStatusDto>?> GetTransfersRequiringReviewAsync(int limit = 50);
    Task<InventoryTransferQueueStatsDto?> GetTransferQueueStatsAsync();
    Task<InventoryTransferQueueStatusDto?> GetTransferQueueStatusAsync(string externalReference);
    Task<bool> CancelQueuedTransferAsync(string externalReference);
    Task<bool> RetryQueuedTransferAsync(string externalReference);

    // Reservations
    Task<List<StockReservationDto>?> GetReservationsAsync(string? sourceSystem = null, string? status = null, int page = 1, int pageSize = 50);
    Task<StockReservationDto?> GetReservationAsync(string reservationId);
    Task<bool> CancelReservationAsync(string reservationId, string? reason = null);

    // Desktop Sales (offline invoicing)
    Task<DesktopSalesListResponse?> GetDesktopSalesAsync(string? warehouseCode = null, string? cardCode = null, string? consolidationStatus = null, DateTime? fromDate = null, DateTime? toDate = null, int page = 1, int pageSize = 50);
    Task<EndOfDayReportDto?> GetEndOfDayReportAsync(DateTime? reportDate = null);

    // Local Stock Snapshots
    Task<LocalStockResultDto?> GetLocalStockAsync(string warehouseCode, DateTime? snapshotDate = null);
    Task<List<string>?> GetMonitoredWarehousesAsync();
    Task<bool> TriggerStockFetchAsync();
    Task<bool> TriggerConsolidationAsync();

    // Posting held sales to SAP by hand. Both return the API's own refusal rather than a bool: every
    // one of them is a sentence the operator has to read — "not fiscalised yet", "already in SAP",
    // "somebody else is posting this" — and a false would collapse them all into "it didn't work".
    Task<(DesktopSalePostResultDto? Result, string? Error)> PostSaleToSapAsync(
        string externalReference, CancellationToken cancellationToken = default);

    Task<(DesktopSalesBulkPostResultDto? Result, string? Error)> PostSalesToSapAsync(
        IReadOnlyList<string> externalReferences, CancellationToken cancellationToken = default);

    // Prices
    Task<ItemPricesByListResponse?> GetPricesByPriceListAsync(int priceListNum, bool forceRefresh = false);
    Task<ItemPricesByListResponse?> GetPricesByBusinessPartnerAsync(string cardCode);
}

public class DesktopIntegrationService : IDesktopIntegrationService
{
    private static string CreditNoteUrl(string reference) =>
        $"api/DesktopIntegration/sales/{Uri.EscapeDataString(reference)}/credit-notes";

    public Task<DesktopCreditForm> PrepareCreditNoteAsync(string reference) =>
        CreditRequestAsync<DesktopCreditForm>(HttpMethod.Get, CreditNoteUrl(reference) + "/prepare");
    public Task<List<DesktopCreditNoteResult>> GetCreditNotesAsync(string reference) =>
        CreditRequestAsync<List<DesktopCreditNoteResult>>(HttpMethod.Get, CreditNoteUrl(reference));
    public Task<DesktopCreditNoteResult> CreateCreditNoteAsync(string reference, CreateDesktopCreditRequest request) =>
        CreditRequestAsync<DesktopCreditNoteResult>(HttpMethod.Post, CreditNoteUrl(reference), request);
    public Task<DesktopCreditNoteResult> ReconcileCreditNoteAsync(string reference, Guid id) =>
        CreditRequestAsync<DesktopCreditNoteResult>(HttpMethod.Post, CreditNoteUrl(reference) + $"/{id}/reconcile");
    public Task<DesktopCreditNoteResult> ContinueCreditNoteAsync(string reference, Guid id) =>
        CreditRequestAsync<DesktopCreditNoteResult>(HttpMethod.Post, CreditNoteUrl(reference) + $"/{id}/continue");

    private async Task<T> CreditRequestAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ReadProblemDetailAsync(response, CancellationToken.None));
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new InvalidOperationException("No credit-note result was returned. Reload the saved notes.");
    }
    private readonly HttpClient _httpClient;
    private readonly ILogger<DesktopIntegrationService> _logger;

    public DesktopIntegrationService(HttpClient httpClient, ILogger<DesktopIntegrationService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<InvoiceQueueStatusDto>?> GetPendingQueueAsync(string? sourceSystem = null, int limit = 100)
    {
        try
        {
            var url = $"api/DesktopIntegration/queue?limit={limit}";
            if (!string.IsNullOrEmpty(sourceSystem))
            {
                url += $"&sourceSystem={Uri.EscapeDataString(sourceSystem)}";
            }
            return await _httpClient.GetFromJsonAsync<List<InvoiceQueueStatusDto>>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pending queue");
            return null;
        }
    }

    public async Task<List<InvoiceQueueStatusDto>?> GetInvoicesRequiringReviewAsync(int limit = 50)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<InvoiceQueueStatusDto>>($"api/DesktopIntegration/queue/review?limit={limit}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting invoices requiring review");
            return null;
        }
    }

    public async Task<InvoiceQueueStatsDto?> GetQueueStatsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<InvoiceQueueStatsDto>("api/DesktopIntegration/queue/stats");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting queue stats");
            return null;
        }
    }

    public async Task<InvoiceQueueStatusDto?> GetQueueStatusAsync(string externalReference)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<InvoiceQueueStatusDto>($"api/DesktopIntegration/queue/{Uri.EscapeDataString(externalReference)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting queue status for {Reference}", externalReference);
            return null;
        }
    }

    public async Task<List<StockReservationDto>?> GetReservationsAsync(string? sourceSystem = null, string? status = null, int page = 1, int pageSize = 50)
    {
        try
        {
            var url = $"api/DesktopIntegration/reservations?page={page}&pageSize={pageSize}&activeOnly=false";
            if (!string.IsNullOrEmpty(sourceSystem))
            {
                url += $"&sourceSystem={Uri.EscapeDataString(sourceSystem)}";
            }
            if (!string.IsNullOrEmpty(status))
            {
                url += $"&status={Uri.EscapeDataString(status)}";
            }
            var response = await _httpClient.GetFromJsonAsync<ReservationListResponse>(url);
            return response?.Reservations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting reservations");
            return null;
        }
    }

    public async Task<StockReservationDto?> GetReservationAsync(string reservationId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<StockReservationDto>($"api/DesktopIntegration/reservations/{Uri.EscapeDataString(reservationId)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting reservation {Id}", reservationId);
            return null;
        }
    }

    public async Task<bool> CancelQueuedInvoiceAsync(string externalReference)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/DesktopIntegration/queue/{Uri.EscapeDataString(externalReference)}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling queued invoice {Reference}", externalReference);
            return false;
        }
    }

    public async Task<bool> RetryQueuedInvoiceAsync(string externalReference)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/DesktopIntegration/queue/{Uri.EscapeDataString(externalReference)}/retry", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrying queued invoice {Reference}", externalReference);
            return false;
        }
    }

    public async Task<bool> CancelReservationAsync(string reservationId, string? reason = null)
    {
        try
        {
            var request = new { ReservationId = reservationId, Reason = reason ?? "Cancelled from web admin" };
            var response = await _httpClient.PostAsJsonAsync("api/DesktopIntegration/reservations/cancel", request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling reservation {Id}", reservationId);
            return false;
        }
    }

    #region Inventory Transfer Queue Methods

    public async Task<List<InventoryTransferQueueStatusDto>?> GetPendingTransferQueueAsync(string? sourceSystem = null, int limit = 100)
    {
        try
        {
            var url = $"api/DesktopIntegration/transfer-queue?limit={limit}";
            if (!string.IsNullOrEmpty(sourceSystem))
            {
                url += $"&sourceSystem={Uri.EscapeDataString(sourceSystem)}";
            }
            return await _httpClient.GetFromJsonAsync<List<InventoryTransferQueueStatusDto>>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pending transfer queue");
            return null;
        }
    }

    public async Task<List<InventoryTransferQueueStatusDto>?> GetTransfersRequiringReviewAsync(int limit = 50)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<InventoryTransferQueueStatusDto>>($"api/DesktopIntegration/transfer-queue/review?limit={limit}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfers requiring review");
            return null;
        }
    }

    public async Task<InventoryTransferQueueStatsDto?> GetTransferQueueStatsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<InventoryTransferQueueStatsDto>("api/DesktopIntegration/transfer-queue/stats");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfer queue stats");
            return null;
        }
    }

    public async Task<InventoryTransferQueueStatusDto?> GetTransferQueueStatusAsync(string externalReference)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<InventoryTransferQueueStatusDto>($"api/DesktopIntegration/transfer-queue/{Uri.EscapeDataString(externalReference)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transfer queue status for {Reference}", externalReference);
            return null;
        }
    }

    public async Task<bool> CancelQueuedTransferAsync(string externalReference)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/DesktopIntegration/transfer-queue/{Uri.EscapeDataString(externalReference)}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling queued transfer {Reference}", externalReference);
            return false;
        }
    }

    public async Task<bool> RetryQueuedTransferAsync(string externalReference)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/DesktopIntegration/transfer-queue/{Uri.EscapeDataString(externalReference)}/retry", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrying queued transfer {Reference}", externalReference);
            return false;
        }
    }

    #endregion

    #region Desktop Sales & Local Stock

    public async Task<DesktopSalesListResponse?> GetDesktopSalesAsync(string? warehouseCode = null, string? cardCode = null, string? consolidationStatus = null, DateTime? fromDate = null, DateTime? toDate = null, int page = 1, int pageSize = 50)
    {
        try
        {
            var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };
            if (!string.IsNullOrEmpty(warehouseCode))
                queryParams.Add($"warehouseCode={Uri.EscapeDataString(warehouseCode)}");
            if (!string.IsNullOrEmpty(cardCode))
                queryParams.Add($"cardCode={Uri.EscapeDataString(cardCode)}");
            if (!string.IsNullOrEmpty(consolidationStatus))
                queryParams.Add($"consolidationStatus={Uri.EscapeDataString(consolidationStatus)}");
            if (fromDate.HasValue)
                queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue)
                queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");

            var url = $"api/DesktopIntegration/sales?{string.Join("&", queryParams)}";
            return await _httpClient.GetFromJsonAsync<DesktopSalesListResponse>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting desktop sales");
            return null;
        }
    }

    public async Task<EndOfDayReportDto?> GetEndOfDayReportAsync(DateTime? reportDate = null)
    {
        try
        {
            var url = "api/DesktopIntegration/end-of-day/report";
            if (reportDate.HasValue)
                url += $"?reportDate={reportDate.Value:yyyy-MM-dd}";
            return await _httpClient.GetFromJsonAsync<EndOfDayReportDto>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting end-of-day report");
            return null;
        }
    }

    public async Task<LocalStockResultDto?> GetLocalStockAsync(string warehouseCode, DateTime? snapshotDate = null)
    {
        try
        {
            var url = $"api/DesktopIntegration/stock/{Uri.EscapeDataString(warehouseCode)}/local";
            if (snapshotDate.HasValue)
                url += $"?snapshotDate={snapshotDate.Value:yyyy-MM-dd}";
            return await _httpClient.GetFromJsonAsync<LocalStockResultDto>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting local stock for {Warehouse}", warehouseCode);
            return null;
        }
    }

    public async Task<List<string>?> GetMonitoredWarehousesAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<string>>("api/DesktopIntegration/stock/monitored-warehouses");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting monitored warehouses");
            return null;
        }
    }

    public async Task<bool> TriggerStockFetchAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync("api/DesktopIntegration/stock/fetch-daily", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering stock fetch");
            return false;
        }
    }

    public async Task<bool> TriggerConsolidationAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync("api/DesktopIntegration/end-of-day/consolidate", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering consolidation");
            return false;
        }
    }

    public async Task<(DesktopSalePostResultDto? Result, string? Error)> PostSaleToSapAsync(
        string externalReference, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.PostAsync(
                $"api/DesktopIntegration/sales/{Uri.EscapeDataString(externalReference)}/post",
                null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // The API's own words. Every refusal on this route names the sale and says what is
                // wrong with it — not fiscalised, already in SAP, being posted by somebody else — and
                // a generic "the post failed" would throw away the only part the operator can act on.
                return (null, await ReadProblemDetailAsync(response, cancellationToken));
            }

            var result = await response.Content.ReadFromJsonAsync<DesktopSalePostResultDto>(cancellationToken);
            return result is null
                ? (null, "The API accepted the post but returned nothing to show for it.")
                : (result, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting desktop sale {ExternalReference} to SAP", externalReference);
            return (null, $"The post could not be sent: {ex.Message}");
        }
    }

    public async Task<(DesktopSalesBulkPostResultDto? Result, string? Error)> PostSalesToSapAsync(
        IReadOnlyList<string> externalReferences, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "api/DesktopIntegration/sales/post-batch",
                new { ExternalReferenceIds = externalReferences },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return (null, await ReadProblemDetailAsync(response, cancellationToken));
            }

            var result = await response.Content.ReadFromJsonAsync<DesktopSalesBulkPostResultDto>(cancellationToken);
            return result is null
                ? (null, "The API accepted the batch but returned nothing to show for it.")
                : (result, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting {Count} desktop sales to SAP", externalReferences.Count);

            // A batch can outlive the client's timeout while still posting, so this is deliberately
            // not phrased as a failure. Re-pressing is safe — the sales that made it replay their
            // documents rather than raising a second — and saying so is more useful than an error.
            return (null,
                $"The batch could not be completed from here: {ex.Message}. Refresh to see what reached SAP, "
                + "then post whatever is still awaiting close — anything already posted will not be posted twice.");
        }
    }

    /// <remarks>
    /// Prefers <c>detail</c> over <c>title</c>: the API's ProblemDetails carries the domain error's
    /// own sentence in detail, while title is the generic status name.
    /// </remarks>
    private static async Task<string> ReadProblemDetailAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemShape>(cancellationToken);

            if (!string.IsNullOrWhiteSpace(problem?.Detail))
            {
                return problem.Detail;
            }

            return string.IsNullOrWhiteSpace(problem?.Title)
                ? $"The API refused the post ({(int)response.StatusCode})."
                : problem.Title;
        }
        catch
        {
            return $"The API refused the post ({(int)response.StatusCode}).";
        }
    }

    private sealed class ProblemShape
    {
        public string? Title { get; set; }
        public string? Detail { get; set; }
    }

    #endregion

    #region Prices

    public async Task<ItemPricesByListResponse?> GetPricesByPriceListAsync(int priceListNum, bool forceRefresh = false)
    {
        try
        {
            if (forceRefresh)
            {
                var syncResponse = await _httpClient.PostAsync($"api/DesktopIntegration/prices/pricelists/{priceListNum}/sync", null);
                if (!syncResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to sync prices for price list {PriceListNum}: {StatusCode}",
                        priceListNum, syncResponse.StatusCode);
                    return null;
                }
            }

            return await _httpClient.GetFromJsonAsync<ItemPricesByListResponse>(
                $"api/DesktopIntegration/prices/pricelists/{priceListNum}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching prices for price list {PriceListNum}", priceListNum);
            return null;
        }
    }

    public async Task<ItemPricesByListResponse?> GetPricesByBusinessPartnerAsync(string cardCode)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<ItemPricesByListResponse>(
                $"api/DesktopIntegration/prices/business-partner/{Uri.EscapeDataString(cardCode)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching prices for business partner {CardCode}", cardCode);
            return null;
        }
    }

    #endregion
}

#region DTOs

public class InvoiceQueueStatusDto
{
    public int QueueId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public string CustomerCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; }
    public string? LastError { get; set; }
    public string? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public string? FiscalDeviceNumber { get; set; }
    public string? FiscalReceiptNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string? WarehouseCode { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int WaitTimeSeconds { get; set; }
    public bool IsComplete { get; set; }
    public bool IsFailed { get; set; }
    public bool CanRetry { get; set; }
    public bool CanCancel { get; set; }
}

public class InvoiceQueueStatsDto
{
    public int TotalQueued { get; set; }
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int RequiresReview { get; set; }
    public int Cancelled { get; set; }
    // The API counts a seventh status the mirror was missing, so
    // Pending + Processing + Completed + Failed + RequiresReview + Cancelled
    // came up short of TotalQueued by however many invoices had been
    // fiscalized. /desktop-transactions shows the split against the total.
    public int Fiscalized { get; set; }
    public DateTime? OldestPendingAge { get; set; }
    public decimal TotalAmountPending { get; set; }
}

public class StockReservationDto
{
    public string ReservationId { get; set; } = string.Empty;
    public string ExternalReferenceId { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CreatedBy { get; set; }
    public int? SAPDocEntry { get; set; }
    public int? SAPDocNum { get; set; }
    public List<StockReservationLineDto> Lines { get; set; } = new();
}

public class StockReservationLineDto
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}

public class ReservationListResponse
{
    public List<StockReservationDto> Reservations { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class InventoryTransferQueueStatusDto
{
    public int QueueId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string FromWarehouse { get; set; } = string.Empty;
    public string ToWarehouse { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; }
    public string? LastError { get; set; }
    public string? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public decimal TotalQuantity { get; set; }
    public int LineCount { get; set; }
    public bool IsTransferRequest { get; set; }
    public int WaitTimeSeconds { get; set; }
    public bool IsComplete { get; set; }
    public bool IsFailed { get; set; }
    public bool CanRetry { get; set; }
    public bool CanCancel { get; set; }
}

public class InventoryTransferQueueStatsDto
{
    public int TotalQueued { get; set; }
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int RequiresReview { get; set; }
    public int Cancelled { get; set; }
    public DateTime? OldestPendingAge { get; set; }
    public decimal TotalQuantityPending { get; set; }
}

// Desktop Sales DTOs
public class DesktopSalesListResponse
{
    public List<DesktopSaleDto> Sales { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasMore { get; set; }
}

public class DesktopSaleDto
{
    public int Id { get; set; }
    public string ExternalReferenceId { get; set; } = string.Empty;
    public string? SourceSystem { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public DateTime DocDate { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string Currency { get; set; } = "ZWG";
    public string FiscalizationStatus { get; set; } = string.Empty;
    public string? FiscalReceiptNumber { get; set; }

    // --- The receipt the device issued ---
    //
    // Sent by the API's DesktopSaleListItemDto and, until the drawer grew a QR panel, dropped on the
    // floor here. The sale's receipt is the only place these live: it was signed under the sale's own
    // external reference rather than a SAP document number, so the invoice this sale becomes carries
    // no QR of its own to fall back on.
    //
    // Every one of these is nullable because the API's is. A `string` here against a `string?` there
    // makes System.Text.Json throw on the whole response, and the page reports "no data" rather than
    // the one field that disagreed.

    public string? FiscalQRCode { get; set; }
    public string? FiscalVerificationCode { get; set; }
    public string? FiscalDeviceNumber { get; set; }
    public string? FiscalDayNo { get; set; }

    public string ConsolidationStatus { get; set; } = string.Empty;
    public int? ConsolidationId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string? PaymentMethod { get; set; }
    public string? PaymentReference { get; set; }
    public decimal AmountPaid { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    // --- Where the sale got to on its way to SAP ---

    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public DateTime? PostedAt { get; set; }
    public int PostingAttempts { get; set; }
    public string? LastPostingError { get; set; }
    public string? PaymentStatus { get; set; }
    public int? PaymentSapDocNum { get; set; }

    /// <summary>
    /// Why this sale may not be posted on request, or null when it may be.
    /// </summary>
    /// <remarks>
    /// Decided by the API, deliberately. The rule is a real one — which sources post per sale, which
    /// reach SAP through the consolidation, when an unfiscalised sale must not be invoiced — and a
    /// copy of it here would be a second rule able to offer a button the API then refuses.
    /// </remarks>
    public string? PostRefusal { get; set; }

    /// <summary>Whether the console should offer this sale a "Post to SAP" button.</summary>
    /// <remarks>
    /// Derived from <see cref="PostRefusal"/> rather than read off the wire, so the button and the
    /// reason beside it cannot disagree.
    /// </remarks>
    public bool CanPostToSap => string.IsNullOrWhiteSpace(PostRefusal);

    public List<DesktopSaleLineDto> Lines { get; set; } = new();
}

/// <summary>
/// What posting one sale to SAP did. Mirrors the API's <c>DesktopSalePostResult</c>.
/// </summary>
public class DesktopSalePostResultDto
{
    public string ExternalReferenceId { get; set; } = string.Empty;

    /// <summary>One of <see cref="DesktopSalePostOutcomes"/>.</summary>
    public string Outcome { get; set; } = string.Empty;

    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// The values <see cref="DesktopSalePostResultDto.Outcome"/> takes, spelled as the API spells them.
/// </summary>
/// <remarks>
/// Constants rather than an enum on this side. The API sends these as strings, and an enum would
/// turn a value this build has not heard of into a deserialization failure — losing the whole batch's
/// outcomes over one row.
/// </remarks>
public static class DesktopSalePostOutcomes
{
    public const string Posted = "Posted";
    public const string AlreadyInSap = "AlreadyInSap";
    public const string InProgress = "InProgress";
    public const string Failed = "Failed";
    public const string NotPostable = "NotPostable";
}

/// <summary>
/// The values <c>DesktopSaleDto.PaymentStatus</c> takes, as the API writes them.
/// </summary>
/// <remarks>
/// Mirrored from the API's <c>DesktopSalePaymentStatuses</c>, which lives in a project this one does
/// not reference. Anything not listed here is a status this build predates, and the page renders it
/// as "not settled yet" rather than falling over — the settlement line is context, not the reason
/// the drawer was opened.
/// </remarks>
public static class DesktopSalePaymentStatuses
{
    public const string Posted = "Posted";

    /// <summary>SAP already showed the invoice settled, so no payment was sent.</summary>
    public const string PostedUnconfirmed = "PostedUnconfirmed";

    public const string Failed = "Failed";

    /// <summary>The tender has no SAP payment means, or a swipe has no configured card code.</summary>
    public const string Unmapped = "Unmapped";
}

/// <summary>
/// What a bulk post did, sale by sale. Mirrors the API's <c>DesktopSalesBulkPostResult</c>.
/// </summary>
/// <remarks>
/// The counts are recomputed from <see cref="Results"/> rather than read off the wire, for the same
/// reason the API derives them: a summary that can disagree with the rows beneath it eventually does.
/// </remarks>
public class DesktopSalesBulkPostResultDto
{
    public List<DesktopSalePostResultDto> Results { get; set; } = new();

    public int Requested => Results.Count;
    public int Posted => Count(DesktopSalePostOutcomes.Posted);
    public int AlreadyInSap => Count(DesktopSalePostOutcomes.AlreadyInSap);
    public int InProgress => Count(DesktopSalePostOutcomes.InProgress);
    public int Failed => Count(DesktopSalePostOutcomes.Failed);
    public int NotPostable => Count(DesktopSalePostOutcomes.NotPostable);

    /// <summary>Everything SAP now holds, however it got there.</summary>
    public int InSap => Posted + AlreadyInSap;

    private int Count(string outcome) =>
        Results.Count(result => string.Equals(result.Outcome, outcome, StringComparison.Ordinal));
}

public class DesktopSaleLineDto
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string? TaxCode { get; set; }
    public decimal DiscountPercent { get; set; }
}

// End of Day Report DTOs
public class EndOfDayReportDto
{
    public DateTime ReportDate { get; set; }
    public DateTime GeneratedAt { get; set; }
    public int TotalSalesCount { get; set; }
    public decimal TotalSalesAmount { get; set; }
    public decimal TotalVatAmount { get; set; }
    public decimal TotalAmountPaid { get; set; }
    public int PostedInvoiceCount { get; set; }
    public int UnpostedInvoiceCount { get; set; }
    public List<BPSummaryDto> BusinessPartnerSummaries { get; set; } = new();
    public List<UnpostedSaleDto> UnpostedSales { get; set; } = new();
}

public class BPSummaryDto
{
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalVat { get; set; }
    public decimal TotalPaid { get; set; }
}

public class UnpostedSaleDto
{
    public int SaleId { get; set; }
    public string ExternalReferenceId { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public decimal Amount { get; set; }
    public string? FiscalReceiptNumber { get; set; }
    public string ConsolidationStatus { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

// Local Stock DTOs
public class LocalStockResultDto
{
    public string WarehouseCode { get; set; } = string.Empty;
    public DateTime SnapshotDate { get; set; }
    public string SnapshotStatus { get; set; } = string.Empty;
    public List<LocalStockItemDto> Items { get; set; } = new();
}

public class LocalStockItemDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal AvailableQuantity { get; set; }
    public decimal OriginalQuantity { get; set; }
    public decimal TransferAdjustment { get; set; }
    public List<LocalStockBatchDto> Batches { get; set; } = new();
}

public class LocalStockBatchDto
{
    public string? BatchNumber { get; set; }
    public decimal AvailableQuantity { get; set; }
    public decimal OriginalQuantity { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

#endregion
