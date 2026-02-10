using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

#region Customer Authentication Models

/// <summary>
/// Customer login request with validation
/// </summary>
public class CustomerLoginRequest
{
    /// <summary>
    /// Customer card code (SAP Business Partner code)
    /// </summary>
    [Required(ErrorMessage = "Customer code is required")]
    [StringLength(50, MinimumLength = 1, ErrorMessage = "Customer code must be between 1 and 50 characters")]
    [RegularExpression(@"^[a-zA-Z0-9\-_]+$", ErrorMessage = "Customer code contains invalid characters")]
    public string CardCode { get; set; } = string.Empty;

    /// <summary>
    /// Customer password
    /// </summary>
    [Required(ErrorMessage = "Password is required")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters")]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Optional email for verification (2FA)
    /// </summary>
    [EmailAddress(ErrorMessage = "Invalid email format")]
    public string? Email { get; set; }
}

/// <summary>
/// Customer login response
/// </summary>
public class CustomerLoginResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public CustomerInfo? Customer { get; set; }
    public bool RequiresTwoFactor { get; set; }
    public string? TwoFactorToken { get; set; }
}

/// <summary>
/// Customer information
/// </summary>
public class CustomerInfo
{
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public decimal Balance { get; set; }
    public string? Currency { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// Customer password change request
/// </summary>
public class CustomerPasswordChangeRequest
{
    [Required(ErrorMessage = "Current password is required")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "New password is required")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters")]
    [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[@$!%*?&])[A-Za-z\d@$!%*?&]{8,}$",
        ErrorMessage = "Password must contain at least one uppercase letter, one lowercase letter, one number, and one special character")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm password is required")]
    [Compare("NewPassword", ErrorMessage = "Passwords do not match")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>
/// Customer password reset request
/// </summary>
public class CustomerPasswordResetRequest
{
    [Required(ErrorMessage = "Customer code is required")]
    public string CardCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Two-factor authentication verification request
/// </summary>
public class CustomerTwoFactorRequest
{
    [Required(ErrorMessage = "Verification code is required")]
    [StringLength(6, MinimumLength = 6, ErrorMessage = "Verification code must be 6 digits")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Verification code must be 6 digits")]
    public string Code { get; set; } = string.Empty;

    [Required]
    public string TwoFactorToken { get; set; } = string.Empty;
}

#endregion

#region Customer Statement Models

/// <summary>
/// Customer statement request
/// </summary>
public class CustomerStatementRequest
{
    [Required]
    public DateTime FromDate { get; set; } = DateTime.Now.AddMonths(-3);

    [Required]
    public DateTime ToDate { get; set; } = DateTime.Now;

    public string? Currency { get; set; }
    public bool IncludeClosedInvoices { get; set; } = false;
}

/// <summary>
/// Customer statement response
/// </summary>
public class CustomerStatementResponse
{
    public CustomerInfo Customer { get; set; } = new();
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public decimal OpeningBalance { get; set; }
    public decimal TotalInvoices { get; set; }
    public decimal TotalPayments { get; set; }
    public decimal TotalCreditNotes { get; set; }
    public decimal ClosingBalance { get; set; }

    public List<StatementLine> Lines { get; set; } = new();

    // Aging summary
    public AgingSummary Aging { get; set; } = new();
}

/// <summary>
/// Statement line item
/// </summary>
public class StatementLine
{
    public DateTime Date { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Description { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance { get; set; }
    public string? Currency { get; set; }
    public string? Status { get; set; }
    public int? DaysOverdue { get; set; }
}

/// <summary>
/// Aging summary for statement
/// </summary>
public class AgingSummary
{
    public decimal Current { get; set; }
    public decimal Days1To30 { get; set; }
    public decimal Days31To60 { get; set; }
    public decimal Days61To90 { get; set; }
    public decimal Over90Days { get; set; }
    public decimal Total { get; set; }
}

/// <summary>
/// Customer invoice summary
/// </summary>
public class CustomerInvoiceSummary
{
    [JsonPropertyName("docEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("docNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("docDate")]
    public DateTime DocDate { get; set; }

    [JsonPropertyName("dueDate")]
    public DateTime? DueDate { get; set; }

    [JsonPropertyName("docTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("paidToDate")]
    public decimal PaidToDate { get; set; }

    [JsonPropertyName("balance")]
    public decimal Balance { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("daysOverdue")]
    public int DaysOverdue { get; set; }
}

/// <summary>
/// Customer payment summary
/// </summary>
public class CustomerPaymentSummary
{
    [JsonPropertyName("docEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("docNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("docDate")]
    public DateTime DocDate { get; set; }

    [JsonPropertyName("docTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("paymentMethod")]
    public string PaymentMethod { get; set; } = string.Empty;

    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

#endregion

#region Security Models

/// <summary>
/// Customer security audit log
/// </summary>
public class CustomerSecurityAuditLog
{
    public int Id { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Details { get; set; }
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>
/// Customer session info
/// </summary>
public class CustomerSession
{
    public string SessionId { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Rate limit tracking
/// </summary>
public class CustomerRateLimitInfo
{
    public string Identifier { get; set; } = string.Empty; // IP or CardCode
    public int AttemptCount { get; set; }
    public DateTime WindowStart { get; set; }
    public DateTime? LockedUntil { get; set; }
    public bool IsLocked => LockedUntil.HasValue && LockedUntil > DateTime.UtcNow;
}

#endregion

#region Customer Portal Dashboard Models

/// <summary>
/// Customer portal dashboard summary
/// </summary>
public class CustomerDashboardSummary
{
    public CustomerInfo Customer { get; set; } = new();
    public decimal AccountBalance { get; set; }
    public int OpenInvoicesCount { get; set; }
    public decimal TotalOutstanding { get; set; }
    public decimal OverdueAmount { get; set; }
    public int OverdueInvoicesCount { get; set; }
    public DateTime? LastPaymentDate { get; set; }
    public decimal LastPaymentAmount { get; set; }
    public List<CustomerInvoiceSummary> RecentInvoices { get; set; } = new();
    public List<CustomerPaymentSummary> RecentPayments { get; set; } = new();
    public AgingSummary Aging { get; set; } = new();
}

#endregion
