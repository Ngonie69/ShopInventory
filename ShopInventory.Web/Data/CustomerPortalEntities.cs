using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Web.Data;

/// <summary>
/// Entity for storing customer portal credentials and security information
/// </summary>
public class CustomerPortalUser
{
    public int Id { get; set; }

    /// <summary>
    /// SAP Business Partner card code
    /// </summary>
    public string CardCode { get; set; } = string.Empty;

    /// <summary>
    /// Customer display name
    /// </summary>
    public string CardName { get; set; } = string.Empty;

    /// <summary>
    /// Customer email for 2FA and notifications
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Whether the customer wants to receive scheduled statements via email
    /// </summary>
    public bool ReceiveStatements { get; set; } = true;

    /// <summary>
    /// Hashed password using BCrypt
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Salt for additional security (used with BCrypt)
    /// </summary>
    public string? PasswordSalt { get; set; }

    /// <summary>
    /// Whether the account is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether 2FA is enabled
    /// </summary>
    public bool TwoFactorEnabled { get; set; } = false;

    /// <summary>
    /// 2FA secret key for TOTP
    /// </summary>
    public string? TwoFactorSecret { get; set; }

    /// <summary>
    /// Email verified flag
    /// </summary>
    public bool EmailVerified { get; set; } = false;

    /// <summary>
    /// Email verification token
    /// </summary>
    public string? EmailVerificationToken { get; set; }

    /// <summary>
    /// Password reset token
    /// </summary>
    public string? PasswordResetToken { get; set; }

    /// <summary>
    /// Password reset token expiry
    /// </summary>
    public DateTime? PasswordResetTokenExpiry { get; set; }

    /// <summary>
    /// Number of failed login attempts
    /// </summary>
    public int FailedLoginAttempts { get; set; } = 0;

    /// <summary>
    /// Account locked until this time
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>
    /// Last successful login timestamp
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Last login IP address
    /// </summary>
    public string? LastLoginIp { get; set; }

    /// <summary>
    /// Last password change date
    /// </summary>
    public DateTime? LastPasswordChangeAt { get; set; }

    /// <summary>
    /// Password expiry date (for forced rotation)
    /// </summary>
    public DateTime? PasswordExpiresAt { get; set; }

    /// <summary>
    /// Account creation date
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last modification date
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Whether account requires password change on next login
    /// </summary>
    public bool MustChangePassword { get; set; } = false;

    /// <summary>
    /// Previous password hashes (for password history enforcement)
    /// </summary>
    public string? PreviousPasswordHashes { get; set; }

    /// <summary>
    /// Account status (Active, Suspended, Locked)
    /// </summary>
    public string Status { get; set; } = "Active";
}

/// <summary>
/// Entity for customer portal security audit logs
/// </summary>
public class CustomerSecurityLog
{
    public int Id { get; set; }

    public string CardCode { get; set; } = string.Empty;

    /// <summary>
    /// Action performed (Login, Logout, PasswordChange, FailedLogin, etc.)
    /// </summary>
    public string Action { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>
    /// Additional details about the action
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// Whether the action was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Failure reason if not successful
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Request ID for correlation
    /// </summary>
    public string? RequestId { get; set; }

    /// <summary>
    /// Geographic location based on IP (if available)
    /// </summary>
    public string? GeoLocation { get; set; }

    /// <summary>
    /// Risk score for the action (0-100)
    /// </summary>
    public int RiskScore { get; set; } = 0;
}

/// <summary>
/// Entity for customer refresh tokens
/// </summary>
public class CustomerRefreshToken
{
    public int Id { get; set; }

    public string CardCode { get; set; } = string.Empty;

    /// <summary>
    /// The refresh token value (hashed)
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Token creation time
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Token expiry time
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether token has been revoked
    /// </summary>
    public bool IsRevoked { get; set; } = false;

    /// <summary>
    /// Revocation time
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// IP address that created the token
    /// </summary>
    public string? CreatedByIp { get; set; }

    /// <summary>
    /// IP address that revoked the token
    /// </summary>
    public string? RevokedByIp { get; set; }

    /// <summary>
    /// Token that replaced this one
    /// </summary>
    public string? ReplacedByToken { get; set; }

    /// <summary>
    /// User agent of the device
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Device fingerprint for detection of token theft
    /// </summary>
    public string? DeviceFingerprint { get; set; }
}

/// <summary>
/// Entity for rate limiting customer portal requests
/// </summary>
public class CustomerRateLimit
{
    public int Id { get; set; }

    /// <summary>
    /// Identifier (IP address or CardCode)
    /// </summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>
    /// Type of identifier (IP, CardCode, Combined)
    /// </summary>
    public string IdentifierType { get; set; } = "IP";

    /// <summary>
    /// Endpoint or action being rate limited
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Number of requests in the current window
    /// </summary>
    public int RequestCount { get; set; } = 0;

    /// <summary>
    /// Start of the current window
    /// </summary>
    public DateTime WindowStart { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// End of the current window
    /// </summary>
    public DateTime WindowEnd { get; set; }

    /// <summary>
    /// Whether currently blocked
    /// </summary>
    public bool IsBlocked { get; set; } = false;

    /// <summary>
    /// Block expiry time
    /// </summary>
    public DateTime? BlockedUntil { get; set; }

    /// <summary>
    /// Number of times blocked
    /// </summary>
    public int BlockCount { get; set; } = 0;
}
