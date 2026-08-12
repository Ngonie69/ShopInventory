using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

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
    /// Last accepted TOTP time step to prevent code reuse within the verification window.
    /// </summary>
    public long? TwoFactorLastUsedTimeStep { get; set; }

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
    /// JSON array of assigned warehouse codes (e.g. ["WH01","WH02"])
    /// </summary>
    public string? AssignedWarehouseCodes { get; set; }

    /// <summary>
    /// Backward-compatible helper — returns first assigned warehouse code.
    /// </summary>
    [NotMapped]
    public string? AssignedWarehouseCode
    {
        get => GetWarehouseCodes().FirstOrDefault();
        set
        {
            if (string.IsNullOrEmpty(value))
                AssignedWarehouseCodes = null;
            else
                AssignedWarehouseCodes = JsonSerializer.Serialize(new[] { value });
        }
    }

    /// <summary>
    /// Deserialize warehouse codes from JSON.
    /// </summary>
    public List<string> GetWarehouseCodes()
    {
        if (string.IsNullOrEmpty(AssignedWarehouseCodes)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(AssignedWarehouseCodes) ?? new(); }
        catch { return new(); }
    }

    /// <summary>
    /// Serialize warehouse codes to JSON.
    /// </summary>
    public void SetWarehouseCodes(List<string>? codes)
    {
        AssignedWarehouseCodes = codes == null || codes.Count == 0 ? null : JsonSerializer.Serialize(codes);
    }

    /// <summary>
    /// JSON array of assigned customer codes (e.g. ["C001","C002"]) for merchandisers
    /// </summary>
    public string? AssignedCustomerCodes { get; set; }

    /// <summary>
    /// Deserialize customer codes from JSON.
    /// </summary>
    public List<string> GetCustomerCodes()
    {
        if (string.IsNullOrEmpty(AssignedCustomerCodes)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(AssignedCustomerCodes) ?? new(); }
        catch { return new(); }
    }

    /// <summary>
    /// Serialize customer codes to JSON.
    /// </summary>
    public void SetCustomerCodes(List<string>? codes)
    {
        AssignedCustomerCodes = codes == null || codes.Count == 0 ? null : JsonSerializer.Serialize(codes);
    }

    /// <summary>
    /// JSON serialized list of granular permissions
    /// </summary>
    public string? Permissions { get; set; }

    /// <summary>
    /// Assigned section for Driver role (e.g. Cheeseman, Factory, Graniteside, Machipisa, Bulawayo)
    /// </summary>
    [MaxLength(50)]
    public string? AssignedSection { get; set; }

    /// <summary>
    /// Assigned business partner code used by ADR and Sales users for van sales processing.
    /// </summary>
    [MaxLength(100)]
    public string? AssignedBusinessPartnerCode { get; set; }

    /// <summary>
    /// Assigned cost centre code used by ADR and Sales users for van sales processing.
    /// </summary>
    [MaxLength(50)]
    public string? AssignedCostCentreCode { get; set; }

    /// <summary>
    /// The warehouse a van draws its stock from — the source of its inventory transfer requests.
    /// </summary>
    /// <remarks>
    /// Not the same thing as <see cref="AssignedWarehouseCodes"/>, which is the van itself (VAN010) and
    /// so the *destination* of those requests. A van is loaded from one depot and one only: the Bulawayo
    /// vans from KEFBYC, the Harare ones from KEFGRC. The handset cannot be trusted to pick it — it used
    /// to send a warehouse *name* typed into a hardcoded list — so it is assigned here, once, per van.
    /// </remarks>
    [MaxLength(50)]
    public string? SupplyingWarehouseCode { get; set; }

    /// <summary>
    /// The ZIMRA fiscal device this user's handset is registered as, for offline trading.
    ///
    /// Null for everyone who does not stamp their own receipts, which is almost everyone — the desktop
    /// and SAP paths fiscalise through the platform's own devices and never need this.
    ///
    /// It must be unique per handset and never shared. A device's receipt chain has exactly one writer;
    /// two handsets on one device id would each sign a different receipt as number N, and ZIMRA refuses
    /// the whole fiscal day when the file is uploaded.
    /// </summary>
    public int? FiscalDeviceId { get; set; }

    /// <summary>
    /// Navigation property for refresh tokens
    /// </summary>
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    /// <summary>
    /// Navigation property for password reset tokens
    /// </summary>
    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = new List<PasswordResetToken>();

    /// <summary>
    /// Navigation property for registered passkeys
    /// </summary>
    public ICollection<PasskeyCredential> Passkeys { get; set; } = new List<PasskeyCredential>();
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
    /// Hash of the refresh token value
    /// </summary>
    [Required]
    [MaxLength(128)]
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
    /// Hash of the token that replaced this one (if rotated)
    /// </summary>
    [MaxLength(128)]
    public string? ReplacedByTokenHash { get; set; }

    /// <summary>
    /// Check if the token is expired
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Check if the token is active (not revoked and not expired)
    /// </summary>
    public bool IsActive => !IsRevoked && !IsExpired;
}
