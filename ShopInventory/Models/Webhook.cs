using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models;

/// <summary>
/// Represents a webhook subscription for event notifications
/// </summary>
public class Webhook
{
    public int Id { get; set; }

    /// <summary>
    /// Name of the webhook subscription
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The URL to send webhook notifications to
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Secret key for HMAC signature verification
    /// </summary>
    [MaxLength(256)]
    public string? Secret { get; set; }

    /// <summary>
    /// Comma-separated list of event types to subscribe to
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Events { get; set; } = string.Empty;

    /// <summary>
    /// Whether the webhook is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Number of retry attempts for failed deliveries
    /// </summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// Timeout in seconds for webhook requests
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Custom headers to send with webhook requests (JSON format)
    /// </summary>
    public string? CustomHeaders { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastTriggeredAt { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
}

/// <summary>
/// Log entry for webhook deliveries
/// </summary>
public class WebhookDelivery
{
    public int Id { get; set; }
    public int WebhookId { get; set; }
    public Webhook Webhook { get; set; } = null!;

    /// <summary>
    /// The event type that triggered the webhook
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// The payload sent to the webhook URL
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// HTTP response status code
    /// </summary>
    public int? ResponseStatusCode { get; set; }

    /// <summary>
    /// Response body from the webhook endpoint
    /// </summary>
    public string? ResponseBody { get; set; }

    /// <summary>
    /// Whether the delivery was successful
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Error message if delivery failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Duration of the webhook request in milliseconds
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// Number of retry attempts made
    /// </summary>
    public int RetryAttempt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Available webhook event types
/// </summary>
public static class WebhookEventTypes
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

    // Sync events
    public const string SapSyncSuccess = "sap.sync.success";
    public const string SapSyncFailed = "sap.sync.failed";
    public const string SapConnectionLost = "sap.connection.lost";
    public const string SapConnectionRestored = "sap.connection.restored";

    public static readonly string[] All = new[]
    {
        InvoiceCreated, InvoicePaid, InvoiceCancelled,
        PaymentReceived, PaymentFailed, PaymentRefunded,
        StockLow, StockOut, StockReplenished, StockTransfer,
        InventoryAdjusted, InventoryReceived,
        CustomerCreated, CustomerUpdated,
        SapSyncSuccess, SapSyncFailed, SapConnectionLost, SapConnectionRestored
    };
}
