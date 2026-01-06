using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>
/// Request to initiate a payment
/// </summary>
public class InitiatePaymentRequest
{
    /// <summary>
    /// Payment provider (PayNow, Innbucks, Ecocash)
    /// </summary>
    [Required]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Payment amount
    /// </summary>
    [Required]
    [Range(0.01, double.MaxValue, ErrorMessage = "Amount must be greater than 0")]
    public decimal Amount { get; set; }

    /// <summary>
    /// Currency code (USD or ZWL)
    /// </summary>
    [Required]
    [RegularExpression("^(USD|ZWL)$", ErrorMessage = "Currency must be USD or ZWL")]
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Phone number for mobile money payments (required for Ecocash/Innbucks)
    /// </summary>
    [Phone]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// Email address for payment notifications
    /// </summary>
    [EmailAddress]
    public string? Email { get; set; }

    /// <summary>
    /// Invoice ID to link this payment to
    /// </summary>
    public int? InvoiceId { get; set; }

    /// <summary>
    /// Customer/Business Partner code
    /// </summary>
    public string? CustomerCode { get; set; }

    /// <summary>
    /// Payment reference or description
    /// </summary>
    [MaxLength(200)]
    public string? Reference { get; set; }

    /// <summary>
    /// URL to redirect after payment (for web-based payments)
    /// </summary>
    [Url]
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// URL for payment status callbacks
    /// </summary>
    [Url]
    public string? CallbackUrl { get; set; }
}

/// <summary>
/// Response from payment initiation
/// </summary>
public class InitiatePaymentResponse
{
    /// <summary>
    /// Internal transaction ID
    /// </summary>
    public int TransactionId { get; set; }

    /// <summary>
    /// External transaction ID from provider
    /// </summary>
    public string? ExternalTransactionId { get; set; }

    /// <summary>
    /// Payment status
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Payment provider used
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// URL to redirect user to complete payment (for web-based payments)
    /// </summary>
    public string? PaymentUrl { get; set; }

    /// <summary>
    /// QR code data for scanning (if applicable)
    /// </summary>
    public string? QrCode { get; set; }

    /// <summary>
    /// USSD code to dial (for mobile money)
    /// </summary>
    public string? UssdCode { get; set; }

    /// <summary>
    /// Instructions for completing the payment
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Expiry time for this payment request
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Additional provider-specific data
    /// </summary>
    public Dictionary<string, object>? ProviderData { get; set; }
}

/// <summary>
/// Payment transaction details
/// </summary>
public class PaymentTransactionDto
{
    public int Id { get; set; }
    public string? ExternalTransactionId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Reference { get; set; }
    public int? InvoiceId { get; set; }
    public string? CustomerCode { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? StatusMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Request to check payment status
/// </summary>
public class CheckPaymentStatusRequest
{
    /// <summary>
    /// Internal transaction ID
    /// </summary>
    public int? TransactionId { get; set; }

    /// <summary>
    /// External transaction ID from provider
    /// </summary>
    public string? ExternalTransactionId { get; set; }
}

/// <summary>
/// Payment status response
/// </summary>
public class PaymentStatusResponse
{
    public int TransactionId { get; set; }
    public string? ExternalTransactionId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? StatusMessage { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Callback payload from payment provider
/// </summary>
public class PaymentCallbackPayload
{
    public string? TransactionId { get; set; }
    public string? ExternalTransactionId { get; set; }
    public string? Status { get; set; }
    public string? StatusMessage { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Provider { get; set; }
    public string? Reference { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Signature { get; set; }
    public Dictionary<string, object>? RawData { get; set; }
}

/// <summary>
/// Payment gateway configuration DTO
/// </summary>
public class PaymentGatewayConfigDto
{
    public int Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsSandbox { get; set; }
    public string? MerchantId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Request to update payment gateway configuration
/// </summary>
public class UpdatePaymentGatewayConfigRequest
{
    public bool? IsEnabled { get; set; }
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public string? MerchantId { get; set; }
    public bool? IsSandbox { get; set; }
    public string? BaseUrl { get; set; }
    public string? DefaultCallbackUrl { get; set; }
}

/// <summary>
/// List of available payment providers
/// </summary>
public class PaymentProvidersResponse
{
    public List<PaymentProviderInfo> Providers { get; set; } = new();
}

/// <summary>
/// Information about a payment provider
/// </summary>
public class PaymentProviderInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsSandbox { get; set; }
    public List<string> SupportedCurrencies { get; set; } = new();
    public List<string> PaymentMethods { get; set; } = new();
    public string? LogoUrl { get; set; }
}

/// <summary>
/// Transaction list response with pagination
/// </summary>
public class PaymentTransactionListResponse
{
    public List<PaymentTransactionDto> Transactions { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public decimal TotalAmount { get; set; }
}
