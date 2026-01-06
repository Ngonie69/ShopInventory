using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Interface for offline queue service
/// </summary>
public interface IOfflineQueueService
{
    Task<QueuedTransactionDto> QueueTransactionAsync(string transactionType, object transactionData, string? createdBy = null, int priority = 5, CancellationToken cancellationToken = default);
    Task<OfflineQueueStatusDto> GetQueueStatusAsync(CancellationToken cancellationToken = default);
    Task ProcessQueueAsync(CancellationToken cancellationToken = default);
    Task<bool> RetryTransactionAsync(int transactionId, CancellationToken cancellationToken = default);
    Task<bool> CancelTransactionAsync(int transactionId, CancellationToken cancellationToken = default);
    Task CleanupCompletedTransactionsAsync(int daysToKeep = 7, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for sync status service
/// </summary>
public interface ISyncStatusService
{
    Task<SyncStatusDashboardDto> GetSyncStatusDashboardAsync(CancellationToken cancellationToken = default);
    Task<SapConnectionStatusDto> CheckSapConnectionAsync(CancellationToken cancellationToken = default);
    Task LogConnectionCheckAsync(bool isSuccess, double? responseTimeMs, string? errorMessage = null, string? endpoint = null, CancellationToken cancellationToken = default);
    Task<SyncHealthSummaryDto> GetHealthSummaryAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Offline queue service implementation
/// </summary>
public class OfflineQueueService : IOfflineQueueService
{
    private readonly ApplicationDbContext _context;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<OfflineQueueService> _logger;

    public OfflineQueueService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        ILogger<OfflineQueueService> logger)
    {
        _context = context;
        _sapClient = sapClient;
        _logger = logger;
    }

    /// <summary>
    /// Queue a transaction for later processing
    /// </summary>
    public async Task<QueuedTransactionDto> QueueTransactionAsync(string transactionType, object transactionData, string? createdBy = null, int priority = 5, CancellationToken cancellationToken = default)
    {
        var summary = GenerateTransactionSummary(transactionType, transactionData);

        var queueItem = new OfflineQueueItem
        {
            TransactionType = transactionType,
            Status = "Pending",
            TransactionData = JsonSerializer.Serialize(transactionData),
            Summary = summary,
            Priority = priority,
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
            NextRetryAt = DateTime.UtcNow
        };

        _context.OfflineQueueItems.Add(queueItem);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Transaction queued: {Type} - {Summary}", transactionType, summary);

        return MapToDto(queueItem);
    }

    /// <summary>
    /// Get current queue status
    /// </summary>
    public async Task<OfflineQueueStatusDto> GetQueueStatusAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _context.OfflineQueueItems
            .Where(q => q.Status == "Pending" || q.Status == "Processing")
            .OrderBy(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        var pendingCount = await _context.OfflineQueueItems.CountAsync(q => q.Status == "Pending", cancellationToken);
        var failedCount = await _context.OfflineQueueItems.CountAsync(q => q.Status == "Failed", cancellationToken);
        var processedCount = await _context.OfflineQueueItems.CountAsync(q => q.Status == "Completed", cancellationToken);

        var oldestPending = await _context.OfflineQueueItems
            .Where(q => q.Status == "Pending")
            .OrderBy(q => q.CreatedAt)
            .Select(q => q.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var lastProcessed = await _context.OfflineQueueItems
            .Where(q => q.Status == "Completed")
            .OrderByDescending(q => q.CompletedAt)
            .Select(q => q.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new OfflineQueueStatusDto
        {
            PendingCount = pendingCount,
            FailedCount = failedCount,
            ProcessedCount = processedCount,
            OldestPendingAt = oldestPending != default ? oldestPending : null,
            LastProcessedAt = lastProcessed,
            PendingTransactions = pending.Select(MapToDto).ToList()
        };
    }

    /// <summary>
    /// Process pending transactions in the queue
    /// </summary>
    public async Task ProcessQueueAsync(CancellationToken cancellationToken = default)
    {
        var pendingItems = await _context.OfflineQueueItems
            .Where(q => q.Status == "Pending" && (q.NextRetryAt == null || q.NextRetryAt <= DateTime.UtcNow))
            .OrderBy(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        foreach (var item in pendingItems)
        {
            await ProcessTransactionAsync(item, cancellationToken);
        }
    }

    /// <summary>
    /// Retry a specific failed transaction
    /// </summary>
    public async Task<bool> RetryTransactionAsync(int transactionId, CancellationToken cancellationToken = default)
    {
        var item = await _context.OfflineQueueItems.FindAsync(new object[] { transactionId }, cancellationToken);
        if (item == null || item.Status != "Failed")
        {
            return false;
        }

        item.Status = "Pending";
        item.NextRetryAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        await ProcessTransactionAsync(item, cancellationToken);
        return true;
    }

    /// <summary>
    /// Cancel a pending transaction
    /// </summary>
    public async Task<bool> CancelTransactionAsync(int transactionId, CancellationToken cancellationToken = default)
    {
        var item = await _context.OfflineQueueItems.FindAsync(new object[] { transactionId }, cancellationToken);
        if (item == null || item.Status == "Completed")
        {
            return false;
        }

        item.Status = "Cancelled";
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Transaction cancelled: {Id}", transactionId);
        return true;
    }

    /// <summary>
    /// Cleanup old completed transactions
    /// </summary>
    public async Task CleanupCompletedTransactionsAsync(int daysToKeep = 7, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-daysToKeep);
        var oldItems = await _context.OfflineQueueItems
            .Where(q => q.Status == "Completed" && q.CompletedAt < cutoff)
            .ToListAsync(cancellationToken);

        _context.OfflineQueueItems.RemoveRange(oldItems);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Cleaned up {Count} old completed transactions", oldItems.Count);
    }

    private async Task ProcessTransactionAsync(OfflineQueueItem item, CancellationToken cancellationToken)
    {
        item.Status = "Processing";
        item.AttemptCount++;
        item.LastAttemptAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            // Process based on transaction type
            var success = item.TransactionType switch
            {
                "Invoice" => await ProcessInvoiceAsync(item, cancellationToken),
                "Payment" => await ProcessPaymentAsync(item, cancellationToken),
                "InventoryTransfer" => await ProcessTransferAsync(item, cancellationToken),
                _ => throw new NotSupportedException($"Unknown transaction type: {item.TransactionType}")
            };

            if (success)
            {
                item.Status = "Completed";
                item.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation("Transaction processed successfully: {Id}", item.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process transaction {Id}: {Message}", item.Id, ex.Message);
            item.LastError = ex.Message;

            if (item.AttemptCount >= item.MaxAttempts)
            {
                item.Status = "Failed";
            }
            else
            {
                item.Status = "Pending";
                // Exponential backoff: 1min, 5min, 15min, 30min, 60min
                var delayMinutes = Math.Pow(2, item.AttemptCount) * 1;
                item.NextRetryAt = DateTime.UtcNow.AddMinutes(Math.Min(delayMinutes, 60));
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ProcessInvoiceAsync(OfflineQueueItem item, CancellationToken cancellationToken)
    {
        // Deserialize and process invoice
        // This would call the SAP client to create the invoice
        _logger.LogInformation("Processing queued invoice: {Id}", item.Id);
        await Task.Delay(100, cancellationToken); // Placeholder
        return true;
    }

    private async Task<bool> ProcessPaymentAsync(OfflineQueueItem item, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing queued payment: {Id}", item.Id);
        await Task.Delay(100, cancellationToken); // Placeholder
        return true;
    }

    private async Task<bool> ProcessTransferAsync(OfflineQueueItem item, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing queued transfer: {Id}", item.Id);
        await Task.Delay(100, cancellationToken); // Placeholder
        return true;
    }

    private static string GenerateTransactionSummary(string transactionType, object transactionData)
    {
        var json = JsonSerializer.Serialize(transactionData);
        var doc = JsonDocument.Parse(json);

        return transactionType switch
        {
            "Invoice" => doc.RootElement.TryGetProperty("CardName", out var cardName)
                ? $"Invoice for {cardName.GetString()}"
                : "Invoice",
            "Payment" => doc.RootElement.TryGetProperty("DocTotal", out var total)
                ? $"Payment of {total.GetDecimal():N2}"
                : "Payment",
            "InventoryTransfer" => doc.RootElement.TryGetProperty("FromWarehouse", out var from) &&
                                   doc.RootElement.TryGetProperty("ToWarehouse", out var to)
                ? $"Transfer from {from.GetString()} to {to.GetString()}"
                : "Inventory Transfer",
            _ => transactionType
        };
    }

    private static QueuedTransactionDto MapToDto(OfflineQueueItem item) => new()
    {
        Id = item.Id,
        TransactionType = item.TransactionType,
        Status = item.Status,
        CreatedAt = item.CreatedAt,
        LastAttemptAt = item.LastAttemptAt,
        AttemptCount = item.AttemptCount,
        LastError = item.LastError,
        CreatedBy = item.CreatedBy,
        Summary = item.Summary
    };
}

/// <summary>
/// Sync status service implementation
/// </summary>
public class SyncStatusService : ISyncStatusService
{
    private readonly ApplicationDbContext _context;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly SAPSettings _sapSettings;
    private readonly ILogger<SyncStatusService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public SyncStatusService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        IOptions<SAPSettings> sapSettings,
        ILogger<SyncStatusService> logger,
        ILoggerFactory loggerFactory)
    {
        _context = context;
        _sapClient = sapClient;
        _sapSettings = sapSettings.Value;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Get complete sync status dashboard
    /// </summary>
    public async Task<SyncStatusDashboardDto> GetSyncStatusDashboardAsync(CancellationToken cancellationToken = default)
    {
        var sapConnection = await CheckSapConnectionAsync(cancellationToken);
        var offlineQueueLogger = _loggerFactory.CreateLogger<OfflineQueueService>();
        var offlineQueueService = new OfflineQueueService(_context, _sapClient, offlineQueueLogger);
        var queueStatus = await offlineQueueService.GetQueueStatusAsync(cancellationToken);
        var healthSummary = await GetHealthSummaryAsync(cancellationToken);

        // Get cache sync statuses
        var cacheStatuses = new List<CacheSyncStatusDto>
        {
            await GetCacheSyncStatusAsync("Products", cancellationToken),
            await GetCacheSyncStatusAsync("Prices", cancellationToken),
            await GetCacheSyncStatusAsync("BusinessPartners", cancellationToken),
            await GetCacheSyncStatusAsync("Warehouses", cancellationToken),
            await GetCacheSyncStatusAsync("GLAccounts", cancellationToken)
        };

        return new SyncStatusDashboardDto
        {
            GeneratedAt = DateTime.UtcNow,
            SapConnection = sapConnection,
            CacheStatuses = cacheStatuses,
            OfflineQueue = queueStatus,
            HealthSummary = healthSummary
        };
    }

    /// <summary>
    /// Check SAP connection status
    /// </summary>
    public async Task<SapConnectionStatusDto> CheckSapConnectionAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        bool isConnected = false;
        string? error = null;

        try
        {
            if (_sapSettings.Enabled)
            {
                // Try to get warehouses as a simple connectivity check
                var warehouses = await _sapClient.GetWarehousesAsync(cancellationToken);
                isConnected = warehouses != null && warehouses.Count > 0;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            isConnected = false;
        }

        stopwatch.Stop();

        // Log the connection check
        await LogConnectionCheckAsync(isConnected, stopwatch.ElapsedMilliseconds, error, "GetWarehouses", cancellationToken);

        // Get recent connection history
        var recentLogs = await _context.SapConnectionLogs
            .OrderByDescending(l => l.CheckedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        var consecutiveFailures = 0;
        foreach (var log in recentLogs)
        {
            if (!log.IsSuccess) consecutiveFailures++;
            else break;
        }

        return new SapConnectionStatusDto
        {
            IsConnected = isConnected,
            Status = !_sapSettings.Enabled ? "Disabled" : isConnected ? "Connected" : "Error",
            LastConnectedAt = recentLogs.FirstOrDefault(l => l.IsSuccess)?.CheckedAt,
            LastErrorAt = recentLogs.FirstOrDefault(l => !l.IsSuccess)?.CheckedAt,
            LastError = error ?? recentLogs.FirstOrDefault(l => !l.IsSuccess)?.ErrorMessage,
            ConsecutiveFailures = consecutiveFailures,
            ResponseTimeMs = isConnected ? stopwatch.ElapsedMilliseconds : null,
            CompanyDb = _sapSettings.CompanyDB
        };
    }

    /// <summary>
    /// Log a connection check result
    /// </summary>
    public async Task LogConnectionCheckAsync(bool isSuccess, double? responseTimeMs, string? errorMessage = null, string? endpoint = null, CancellationToken cancellationToken = default)
    {
        var log = new SapConnectionLog
        {
            IsSuccess = isSuccess,
            ResponseTimeMs = responseTimeMs,
            ErrorMessage = errorMessage,
            Endpoint = endpoint,
            CheckedAt = DateTime.UtcNow
        };

        _context.SapConnectionLogs.Add(log);
        await _context.SaveChangesAsync(cancellationToken);

        // Keep only last 100 logs
        var oldLogs = await _context.SapConnectionLogs
            .OrderByDescending(l => l.CheckedAt)
            .Skip(100)
            .ToListAsync(cancellationToken);

        if (oldLogs.Any())
        {
            _context.SapConnectionLogs.RemoveRange(oldLogs);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Get overall health summary
    /// </summary>
    public async Task<SyncHealthSummaryDto> GetHealthSummaryAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        var recommendations = new List<string>();
        var healthScore = 100;

        // Check SAP connection
        var recentLogs = await _context.SapConnectionLogs
            .OrderByDescending(l => l.CheckedAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        var failureRate = recentLogs.Count > 0 ? (double)recentLogs.Count(l => !l.IsSuccess) / recentLogs.Count : 0;
        if (failureRate > 0.5)
        {
            healthScore -= 30;
            issues.Add("SAP connection is unstable");
            recommendations.Add("Check SAP Service Layer connectivity and credentials");
        }

        // Check offline queue
        var pendingCount = await _context.OfflineQueueItems.CountAsync(q => q.Status == "Pending", cancellationToken);
        var failedCount = await _context.OfflineQueueItems.CountAsync(q => q.Status == "Failed", cancellationToken);

        if (pendingCount > 10)
        {
            healthScore -= 15;
            issues.Add($"{pendingCount} transactions pending in queue");
            recommendations.Add("Process queued transactions when SAP connection is restored");
        }

        if (failedCount > 0)
        {
            healthScore -= 10;
            issues.Add($"{failedCount} transactions failed");
            recommendations.Add("Review and retry failed transactions");
        }

        // Determine overall health status
        string overallHealth;
        if (healthScore >= 80) overallHealth = "Healthy";
        else if (healthScore >= 50) overallHealth = "Warning";
        else overallHealth = "Critical";

        return new SyncHealthSummaryDto
        {
            OverallHealth = overallHealth,
            HealthScore = Math.Max(0, healthScore),
            Issues = issues,
            Recommendations = recommendations
        };
    }

    private async Task<CacheSyncStatusDto> GetCacheSyncStatusAsync(string cacheKey, CancellationToken cancellationToken)
    {
        // This would typically check the web app's cache status
        // For now, return placeholder data
        return await Task.FromResult(new CacheSyncStatusDto
        {
            CacheKey = cacheKey,
            DisplayName = cacheKey,
            LastSyncedAt = DateTime.UtcNow.AddMinutes(-30),
            ItemCount = 0,
            IsStale = false,
            StaleMinutes = 0,
            Status = "Synced"
        });
    }
}
