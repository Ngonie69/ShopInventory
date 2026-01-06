using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models;

/// <summary>
/// Represents a payment transaction from payment gateways
/// </summary>
public class PaymentTransaction
{
    public int Id { get; set; }

    /// <summary>
    /// External transaction ID from payment provider
    /// </summary>
    [MaxLength(100)]
    public string? ExternalTransactionId { get; set; }

    /// <summary>
    /// Payment gateway provider (PayNow, Innbucks, Ecocash)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Payment method used
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string PaymentMethod { get; set; } = string.Empty;

    /// <summary>
    /// Amount in local currency (ZWL or USD)
    /// </summary>
    [Required]
    public decimal Amount { get; set; }

    /// <summary>
    /// Currency code (ZWL, USD)
    /// </summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Phone number for mobile money payments
    /// </summary>
    [MaxLength(20)]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// Reference number for the payment
    /// </summary>
    [MaxLength(100)]
    public string? Reference { get; set; }

    /// <summary>
    /// Related invoice ID if applicable
    /// </summary>
    public int? InvoiceId { get; set; }

    /// <summary>
    /// Customer/Business Partner code
    /// </summary>
    [MaxLength(50)]
    public string? CustomerCode { get; set; }

    /// <summary>
    /// Payment status (Pending, Success, Failed, Cancelled, Refunded)
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// Status message or error description
    /// </summary>
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Webhook/callback URL for payment notifications
    /// </summary>
    [MaxLength(500)]
    public string? CallbackUrl { get; set; }

    /// <summary>
    /// Raw response from payment provider
    /// </summary>
    public string? ProviderResponse { get; set; }

    /// <summary>
    /// User who initiated the transaction
    /// </summary>
    [MaxLength(50)]
    public string? InitiatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Payment gateway configuration
/// </summary>
public class PaymentGatewayConfig
{
    public int Id { get; set; }

    /// <summary>
    /// Provider name (PayNow, Innbucks, Ecocash)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Whether this gateway is enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// API key or integration ID
    /// </summary>
    [MaxLength(256)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// API secret or integration key
    /// </summary>
    [MaxLength(256)]
    public string? ApiSecret { get; set; }

    /// <summary>
    /// Merchant ID or account identifier
    /// </summary>
    [MaxLength(100)]
    public string? MerchantId { get; set; }

    /// <summary>
    /// Whether to use sandbox/test mode
    /// </summary>
    public bool IsSandbox { get; set; } = false;

    /// <summary>
    /// Base URL for the payment API
    /// </summary>
    [MaxLength(256)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Default callback URL for payment notifications
    /// </summary>
    [MaxLength(500)]
    public string? DefaultCallbackUrl { get; set; }

    /// <summary>
    /// Additional configuration in JSON format
    /// </summary>
    public string? AdditionalConfig { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Supported payment providers
/// </summary>
public static class PaymentProviders
{
    public const string PayNow = "PayNow";
    public const string Innbucks = "Innbucks";
    public const string Ecocash = "Ecocash";

    public static readonly string[] All = { PayNow, Innbucks, Ecocash };
}

/// <summary>
/// Payment status values
/// </summary>
public static class PaymentStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Refunded = "Refunded";
    public const string Expired = "Expired";
}
