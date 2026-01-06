using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models;

/// <summary>
/// User entity for authentication
/// </summary>
public class User
{
    /// <summary>
    /// Unique identifier for the user
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Username (unique, used for login)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public required string Username { get; set; }

    /// <summary>
    /// Email address (unique)
    /// </summary>
    [MaxLength(255)]
    public string? Email { get; set; }

    /// <summary>
    /// BCrypt hashed password
    /// </summary>
    [Required]
    [MaxLength(255)]
    public required string PasswordHash { get; set; }

    /// <summary>
    /// User role (Admin, User, ApiUser)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public required string Role { get; set; }

    /// <summary>
    /// First name
    /// </summary>
    [MaxLength(100)]
    public string? FirstName { get; set; }

    /// <summary>
    /// Last name
    /// </summary>
    [MaxLength(100)]
    public string? LastName { get; set; }

    /// <summary>
    /// Whether the user account is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether the user's email has been verified
    /// </summary>
    public bool EmailVerified { get; set; } = false;

    /// <summary>
    /// Number of failed login attempts
    /// </summary>
    public int FailedLoginAttempts { get; set; } = 0;

    /// <summary>
    /// Account lockout end time (null if not locked)
    /// </summary>
    public DateTime? LockoutEnd { get; set; }

    /// <summary>
    /// Date and time the user was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date and time the user was last updated
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Date and time of last login
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Whether two-factor authentication is enabled
    /// </summary>
    public bool TwoFactorEnabled { get; set; } = false;

    /// <summary>
    /// TOTP secret key for 2FA (encrypted)
    /// </summary>
    [MaxLength(256)]
    public string? TwoFactorSecret { get; set; }

    /// <summary>
    /// Backup codes for 2FA recovery (JSON array, encrypted)
    /// </summary>
    public string? TwoFactorBackupCodes { get; set; }

    /// <summary>
    /// Phone number for SMS 2FA (future use)
    /// </summary>
    [MaxLength(20)]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// Whether phone number has been verified
    /// </summary>
    public bool PhoneNumberVerified { get; set; } = false;

    /// <summary>
    /// JSON serialized list of granular permissions
    /// </summary>
    public string? Permissions { get; set; }

    /// <summary>
    /// Navigation property for refresh tokens
    /// </summary>
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    /// <summary>
    /// Navigation property for password reset tokens
    /// </summary>
    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = new List<PasswordResetToken>();
}

/// <summary>
/// Password reset token entity
/// </summary>
public class PasswordResetToken
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The reset token value (hashed)
    /// </summary>
    [Required]
    [MaxLength(256)]
    public required string TokenHash { get; set; }

    /// <summary>
    /// User ID this token belongs to
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Navigation property to User
    /// </summary>
    public User? User { get; set; }

    /// <summary>
    /// When the token was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the token expires
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether the token has been used
    /// </summary>
    public bool IsUsed { get; set; } = false;

    /// <summary>
    /// When the token was used (if applicable)
    /// </summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>
    /// IP address that requested this token
    /// </summary>
    [MaxLength(45)]
    public string? RequestedByIp { get; set; }

    /// <summary>
    /// Check if the token is valid
    /// </summary>
    public bool IsValid => !IsUsed && DateTime.UtcNow < ExpiresAt;
}

/// <summary>
/// Refresh token entity for token management
/// </summary>
public class RefreshToken
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The refresh token value
    /// </summary>
    [Required]
    [MaxLength(500)]
    public required string Token { get; set; }

    /// <summary>
    /// User ID this token belongs to
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Navigation property to User
    /// </summary>
    public User? User { get; set; }

    /// <summary>
    /// When the token was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the token expires
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether the token has been revoked
    /// </summary>
    public bool IsRevoked { get; set; } = false;

    /// <summary>
    /// When the token was revoked (if applicable)
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// IP address that created this token
    /// </summary>
    [MaxLength(45)]
    public string? CreatedByIp { get; set; }

    /// <summary>
    /// IP address that revoked this token (if applicable)
    /// </summary>
    [MaxLength(45)]
    public string? RevokedByIp { get; set; }

    /// <summary>
    /// Reason the token was revoked
    /// </summary>
    [MaxLength(200)]
    public string? ReasonRevoked { get; set; }

    /// <summary>
    /// The token that replaced this one (if rotated)
    /// </summary>
    [MaxLength(500)]
    public string? ReplacedByToken { get; set; }

    /// <summary>
    /// Check if the token is expired
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Check if the token is active (not revoked and not expired)
    /// </summary>
    public bool IsActive => !IsRevoked && !IsExpired;
}
