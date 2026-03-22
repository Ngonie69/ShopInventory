namespace ShopInventory.Web.Models;

#region Exchange Rate Models

/// <summary>
/// DTO for Exchange Rate
/// </summary>
public class ExchangeRateDto
{
    public int Id { get; set; }
    public string FromCurrency { get; set; } = null!;
    public string ToCurrency { get; set; } = null!;
    public decimal Rate { get; set; }
    public decimal InverseRate { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string? Source { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Request to create/update exchange rate
/// </summary>
public class UpsertExchangeRateRequest
{
    public string FromCurrency { get; set; } = null!;
    public string ToCurrency { get; set; } = null!;
    public decimal Rate { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public string? Source { get; set; }
}

/// <summary>
/// Exchange rate history response
/// </summary>
public class ExchangeRateHistoryResponse
{
    public string FromCurrency { get; set; } = null!;
    public string ToCurrency { get; set; } = null!;
    public List<ExchangeRateDto> History { get; set; } = new();
}

#endregion

#region Backup Models

/// <summary>
/// DTO for Backup
/// </summary>
public class BackupDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = null!;
    public string? FilePath { get; set; }
    public long SizeBytes { get; set; }
    public string SizeFormatted => FormatSize(SizeBytes);
    public string BackupType { get; set; } = "Full";
    public string Status { get; set; } = "InProgress";
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration => CompletedAt?.Subtract(StartedAt);
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public string? Description { get; set; }
    public bool IsOffsite { get; set; }
    public string? CloudUrl { get; set; }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Request to create a backup
/// </summary>
public class CreateBackupRequest
{
    public string BackupType { get; set; } = "Full";
    public string? Description { get; set; }
    public bool UploadToCloud { get; set; } = false;
}

/// <summary>
/// Backup list response
/// </summary>
public class BackupListResponse
{
    public int TotalCount { get; set; }
    public List<BackupDto> Backups { get; set; } = new();
}

/// <summary>
/// Backup statistics
/// </summary>
public class BackupStatsDto
{
    public int TotalBackups { get; set; }
    public int SuccessfulBackups { get; set; }
    public int FailedBackups { get; set; }
    public long TotalSizeBytes { get; set; }
    public string TotalSizeFormatted { get; set; } = "0 B";
    public DateTime? LastBackupAt { get; set; }
    public DateTime? NextScheduledBackup { get; set; }
    public int BackupsLast24Hours { get; set; }
    public int BackupsLast7Days { get; set; }
}

#endregion

#region Webhook Models

/// <summary>
/// DTO for webhook information
/// </summary>
public class WebhookDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public List<string> Events { get; set; } = new();
    public bool IsActive { get; set; }
    public int RetryCount { get; set; }
    public int TimeoutSeconds { get; set; }
    public Dictionary<string, string>? CustomHeaders { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastTriggeredAt { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
}

/// <summary>
/// Request to create a new webhook subscription
/// </summary>
public class CreateWebhookRequest
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Secret { get; set; }
    public List<string> Events { get; set; } = new();
    public int RetryCount { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 30;
    public Dictionary<string, string>? CustomHeaders { get; set; }
}

/// <summary>
/// Request to update a webhook subscription
/// </summary>
public class UpdateWebhookRequest
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public string? Secret { get; set; }
    public List<string>? Events { get; set; }
    public bool? IsActive { get; set; }
    public int? RetryCount { get; set; }
    public int? TimeoutSeconds { get; set; }
    public Dictionary<string, string>? CustomHeaders { get; set; }
}

/// <summary>
/// Webhook delivery log entry
/// </summary>
public class WebhookDeliveryDto
{
    public int Id { get; set; }
    public int WebhookId { get; set; }
    public string WebhookName { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public int? ResponseStatusCode { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public long DurationMs { get; set; }
    public int RetryAttempt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Webhook list response
/// </summary>
public class WebhookListResponse
{
    public int TotalCount { get; set; }
    public List<WebhookDto> Webhooks { get; set; } = new();
}

/// <summary>
/// Webhook delivery list response
/// </summary>
public class WebhookDeliveryListResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<WebhookDeliveryDto> Deliveries { get; set; } = new();
}

/// <summary>
/// Response containing a list of webhook event types from the API
/// </summary>
public class WebhookEventTypesResponse
{
    public List<WebhookEventTypeInfo> EventTypes { get; set; } = new();
}

/// <summary>
/// Information about a webhook event type
/// </summary>
public class WebhookEventTypeInfo
{
    public string EventType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Available webhook events - must match backend WebhookEventTypes
/// </summary>
public static class WebhookEvents
{
    // Invoice events
    public const string InvoiceCreated = "invoice.created";
    public const string InvoicePaid = "invoice.paid";
    public const string InvoiceCancelled = "invoice.cancelled";

    // Payment events
    public const string PaymentReceived = "payment.received";
    public const string PaymentFailed = "payment.failed";
    public const string PaymentRefunded = "payment.refunded";

    // Stock events
    public const string StockLow = "stock.low";
    public const string StockOut = "stock.out";
    public const string StockReplenished = "stock.replenished";
    public const string StockTransfer = "stock.transfer";

    // Inventory events
    public const string InventoryAdjusted = "inventory.adjusted";
    public const string InventoryReceived = "inventory.received";

    // Customer events
    public const string CustomerCreated = "customer.created";
    public const string CustomerUpdated = "customer.updated";

    // SAP events
    public const string SapSyncSuccess = "sap.sync.success";
    public const string SapSyncFailed = "sap.sync.failed";
    public const string SapConnectionLost = "sap.connection.lost";
    public const string SapConnectionRestored = "sap.connection.restored";

    public static List<string> All => new()
    {
        InvoiceCreated, InvoicePaid, InvoiceCancelled,
        PaymentReceived, PaymentFailed, PaymentRefunded,
        StockLow, StockOut, StockReplenished, StockTransfer,
        InventoryAdjusted, InventoryReceived,
        CustomerCreated, CustomerUpdated,
        SapSyncSuccess, SapSyncFailed, SapConnectionLost, SapConnectionRestored
    };

    /// <summary>
    /// Group events by category for display
    /// </summary>
    public static Dictionary<string, List<(string EventType, string Description)>> Grouped => new()
    {
        ["Invoice"] = new()
        {
            (InvoiceCreated, "New invoice is created"),
            (InvoicePaid, "Invoice is fully paid"),
            (InvoiceCancelled, "Invoice is cancelled")
        },
        ["Payment"] = new()
        {
            (PaymentReceived, "Payment is received"),
            (PaymentFailed, "Payment fails"),
            (PaymentRefunded, "Payment is refunded")
        },
        ["Stock"] = new()
        {
            (StockLow, "Stock falls below minimum level"),
            (StockOut, "Stock reaches zero"),
            (StockReplenished, "Stock is replenished"),
            (StockTransfer, "Stock transferred between warehouses")
        },
        ["Inventory"] = new()
        {
            (InventoryAdjusted, "Inventory is adjusted"),
            (InventoryReceived, "Inventory is received")
        },
        ["Customer"] = new()
        {
            (CustomerCreated, "New customer is created"),
            (CustomerUpdated, "Customer details are updated")
        },
        ["SAP"] = new()
        {
            (SapSyncSuccess, "SAP sync completes successfully"),
            (SapSyncFailed, "SAP sync fails"),
            (SapConnectionLost, "SAP connection is lost"),
            (SapConnectionRestored, "SAP connection is restored")
        }
    };
}

#endregion
