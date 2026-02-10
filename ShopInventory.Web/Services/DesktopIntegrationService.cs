using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace ShopInventory.Web.Services;

/// <summary>
/// Service for accessing desktop integration data (invoices, transfers, reservations, queues)
/// </summary>
public interface IDesktopIntegrationService
{
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
}

public class DesktopIntegrationService : IDesktopIntegrationService
{
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

#endregion
