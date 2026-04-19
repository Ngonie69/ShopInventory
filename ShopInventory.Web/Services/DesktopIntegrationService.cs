using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using ShopInventory.Web.Models;

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

    // Desktop Sales (offline invoicing)
    Task<DesktopSalesListResponse?> GetDesktopSalesAsync(string? warehouseCode = null, string? cardCode = null, string? consolidationStatus = null, DateTime? fromDate = null, DateTime? toDate = null, int page = 1, int pageSize = 50);
    Task<EndOfDayReportDto?> GetEndOfDayReportAsync(DateTime? reportDate = null);

    // Local Stock Snapshots
    Task<LocalStockResultDto?> GetLocalStockAsync(string warehouseCode, DateTime? snapshotDate = null);
    Task<List<string>?> GetMonitoredWarehousesAsync();
    Task<bool> TriggerStockFetchAsync();
    Task<bool> TriggerConsolidationAsync();

    // Prices
    Task<ItemPricesByListResponse?> GetPricesByPriceListAsync(int priceListNum, bool forceRefresh = false);
    Task<ItemPricesByListResponse?> GetPricesByBusinessPartnerAsync(string cardCode);

    // Reports
    Task<FiscalizedSalesReportResult?> GetFiscalizedSalesReportAsync(
        string period = "Daily", DateTime? date = null, DateTime? fromDate = null,
        DateTime? toDate = null, string? cardCode = null, string? warehouseCode = null,
        bool? isConsolidated = null, int page = 1, int pageSize = 50);
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

    #endregion

    #region Prices

    public async Task<ItemPricesByListResponse?> GetPricesByPriceListAsync(int priceListNum, bool forceRefresh = false)
    {
        try
        {
            var url = $"api/DesktopIntegration/prices/pricelists/{priceListNum}";
            if (forceRefresh) url += "?forceRefresh=true";
            return await _httpClient.GetFromJsonAsync<ItemPricesByListResponse>(url);
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

    public async Task<FiscalizedSalesReportResult?> GetFiscalizedSalesReportAsync(
        string period = "Daily", DateTime? date = null, DateTime? fromDate = null,
        DateTime? toDate = null, string? cardCode = null, string? warehouseCode = null,
        bool? isConsolidated = null, int page = 1, int pageSize = 50)
    {
        try
        {
            var queryParams = new List<string> { $"period={Uri.EscapeDataString(period)}", $"page={page}", $"pageSize={pageSize}" };
            if (date.HasValue) queryParams.Add($"date={date.Value:yyyy-MM-dd}");
            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (!string.IsNullOrEmpty(cardCode)) queryParams.Add($"cardCode={Uri.EscapeDataString(cardCode)}");
            if (!string.IsNullOrEmpty(warehouseCode)) queryParams.Add($"warehouseCode={Uri.EscapeDataString(warehouseCode)}");
            if (isConsolidated.HasValue) queryParams.Add($"isConsolidated={isConsolidated.Value.ToString().ToLower()}");

            var url = $"api/DesktopIntegration/reports/fiscalized-sales?{string.Join("&", queryParams)}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError("Fiscalized sales report API returned {StatusCode}: {Body}", response.StatusCode, body);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<FiscalizedSalesReportResult>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching fiscalized sales report");
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
    public string ConsolidationStatus { get; set; } = string.Empty;
    public int? ConsolidationId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string? PaymentMethod { get; set; }
    public string? PaymentReference { get; set; }
    public decimal AmountPaid { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<DesktopSaleLineDto> Lines { get; set; } = new();
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

// Fiscalized Sales Report DTOs
public class FiscalizedSalesReportResult
{
    public DateTime GeneratedAtUtc { get; set; }
    public string Period { get; set; } = string.Empty;
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public FiscalizedSalesReportSummary Summary { get; set; } = new();
    public List<DailyBreakdownDto> DailyBreakdown { get; set; } = new();
    public List<FiscalizedSaleDto> Sales { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasMore { get; set; }
}

public class FiscalizedSalesReportSummary
{
    public int TotalFiscalizedSales { get; set; }
    public int ConsolidatedCount { get; set; }
    public int AwaitingConsolidationCount { get; set; }
    public decimal TotalSalesAmount { get; set; }
    public decimal TotalVatAmount { get; set; }
    public int UniqueCustomers { get; set; }
    public int UniqueWarehouses { get; set; }
    public Dictionary<string, decimal> AmountByWarehouse { get; set; } = new();
    public Dictionary<string, decimal> AmountByCustomer { get; set; } = new();
}

public class DailyBreakdownDto
{
    public DateTime Date { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal VatAmount { get; set; }
    public int ConsolidatedCount { get; set; }
    public int AwaitingConsolidationCount { get; set; }
}

public class FiscalizedSaleDto
{
    public int QueueId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string CustomerCode { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? WarehouseCode { get; set; }
    public string? FiscalDeviceNumber { get; set; }
    public string? FiscalReceiptNumber { get; set; }
    public bool? FiscalizationSuccess { get; set; }
    public string? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public bool IsConsolidated { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FiscalizedAt { get; set; }
    public DateTime? ConsolidatedAt { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string? CreatedBy { get; set; }
    public string? Notes { get; set; }
    public List<FiscalizedSaleLineDto> Lines { get; set; } = new();
}

public class FiscalizedSaleLineDto
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public decimal DiscountPercent { get; set; }
    public string? WarehouseCode { get; set; }
    public string? TaxCode { get; set; }
    public string? UoMCode { get; set; }
}

#endregion
