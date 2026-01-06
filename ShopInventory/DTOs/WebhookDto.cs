using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

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
    /// <summary>
    /// Name of the webhook subscription
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The URL to send webhook notifications to (must be HTTPS)
    /// </summary>
    [Required]
    [Url]
    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Secret key for HMAC signature verification
    /// </summary>
    [MaxLength(256)]
    public string? Secret { get; set; }

    /// <summary>
    /// List of event types to subscribe to
    /// </summary>
    [Required]
    [MinLength(1)]
    public List<string> Events { get; set; } = new();

    /// <summary>
    /// Number of retry attempts for failed deliveries (1-10)
    /// </summary>
    [Range(1, 10)]
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// Timeout in seconds for webhook requests (5-60)
    /// </summary>
    [Range(5, 60)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Custom headers to send with webhook requests
    /// </summary>
    public Dictionary<string, string>? CustomHeaders { get; set; }
}

/// <summary>
/// Request to update a webhook subscription
/// </summary>
public class UpdateWebhookRequest
{
    [MaxLength(100)]
    public string? Name { get; set; }

    [Url]
    [MaxLength(500)]
    public string? Url { get; set; }

    [MaxLength(256)]
    public string? Secret { get; set; }

    public List<string>? Events { get; set; }

    public bool? IsActive { get; set; }

    [Range(1, 10)]
    public int? RetryCount { get; set; }

    [Range(5, 60)]
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
/// Response containing a list of webhook deliveries
/// </summary>
public class WebhookDeliveryListResponse
{
    public List<WebhookDeliveryDto> Deliveries { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// Available webhook event types
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
/// Request to test a webhook
/// </summary>
public class TestWebhookRequest
{
    /// <summary>
    /// Event type to simulate
    /// </summary>
    [Required]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Sample data to include in the test payload
    /// </summary>
    public object? SampleData { get; set; }
}

/// <summary>
/// Result of a webhook test
/// </summary>
public class TestWebhookResponse
{
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string? ErrorMessage { get; set; }
    public long DurationMs { get; set; }
}
