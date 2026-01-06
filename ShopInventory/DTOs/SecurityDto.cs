using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

#region Two-Factor Authentication DTOs

/// <summary>
/// Response when enabling 2FA - contains setup information
/// </summary>
public class TwoFactorSetupResponse
{
    /// <summary>
    /// Base32 encoded secret key
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// QR code data URI for scanning with authenticator app
    /// </summary>
    public string QrCodeUri { get; set; } = string.Empty;

    /// <summary>
    /// Manual entry key for authenticator apps
    /// </summary>
    public string ManualEntryKey { get; set; } = string.Empty;

    /// <summary>
    /// Backup codes for account recovery
    /// </summary>
    public List<string> BackupCodes { get; set; } = new();
}

/// <summary>
/// Request to verify and enable 2FA
/// </summary>
public class TwoFactorEnableRequest
{
    /// <summary>
    /// The 6-digit TOTP code from authenticator app
    /// </summary>
    [Required]
    [StringLength(6, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Request to verify 2FA during login
/// </summary>
public class TwoFactorVerifyRequest
{
    /// <summary>
    /// The 6-digit TOTP code or backup code
    /// </summary>
    [Required]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a backup code
    /// </summary>
    public bool IsBackupCode { get; set; } = false;
}

/// <summary>
/// Response requiring 2FA verification
/// </summary>
public class TwoFactorRequiredResponse
{
    public bool RequiresTwoFactor { get; set; } = true;
    public string TwoFactorToken { get; set; } = string.Empty;
    public string Message { get; set; } = "Two-factor authentication required";
}

/// <summary>
/// Request to disable 2FA
/// </summary>
public class TwoFactorDisableRequest
{
    /// <summary>
    /// Current password for verification
    /// </summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The 6-digit TOTP code for verification
    /// </summary>
    [Required]
    [StringLength(6, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// 2FA status response
/// </summary>
public class TwoFactorStatusResponse
{
    public bool IsEnabled { get; set; }
    public int BackupCodesRemaining { get; set; }
    public DateTime? EnabledAt { get; set; }
}

#endregion

#region Password Reset DTOs

/// <summary>
/// Request to initiate password reset
/// </summary>
public class PasswordResetRequest
{
    /// <summary>
    /// Email address associated with the account
    /// </summary>
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Request to complete password reset
/// </summary>
public class PasswordResetCompleteRequest
{
    /// <summary>
    /// Reset token from email
    /// </summary>
    [Required]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// New password
    /// </summary>
    [Required]
    [MinLength(8)]
    public string NewPassword { get; set; } = string.Empty;

    /// <summary>
    /// Confirm new password
    /// </summary>
    [Required]
    [Compare(nameof(NewPassword))]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>
/// Request to change password (when logged in)
/// </summary>
public class ChangePasswordRequest
{
    /// <summary>
    /// Current password
    /// </summary>
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    /// <summary>
    /// New password
    /// </summary>
    [Required]
    [MinLength(8)]
    public string NewPassword { get; set; } = string.Empty;

    /// <summary>
    /// Confirm new password
    /// </summary>
    [Required]
    [Compare(nameof(NewPassword))]
    public string ConfirmPassword { get; set; } = string.Empty;
}

#endregion

#region Enhanced User Management DTOs

/// <summary>
/// Detailed user DTO with permissions and 2FA status
/// </summary>
public class UserDetailDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool EmailVerified { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public bool IsLockedOut { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public List<string> Permissions { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// Request to create a new user with permissions
/// </summary>
public class CreateUserDetailRequest
{
    [Required]
    [StringLength(50, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    public string Password { get; set; } = string.Empty;

    [StringLength(100)]
    public string? FirstName { get; set; }

    [StringLength(100)]
    public string? LastName { get; set; }

    [Required]
    public string Role { get; set; } = "User";

    /// <summary>
    /// Custom permissions (if null, defaults to role permissions)
    /// </summary>
    public List<string>? Permissions { get; set; }

    /// <summary>
    /// Whether to send welcome email
    /// </summary>
    public bool SendWelcomeEmail { get; set; } = true;
}

/// <summary>
/// Request to update a user
/// </summary>
public class UpdateUserDetailRequest
{
    [EmailAddress]
    public string? Email { get; set; }

    [StringLength(100)]
    public string? FirstName { get; set; }

    [StringLength(100)]
    public string? LastName { get; set; }

    public string? Role { get; set; }

    public bool? IsActive { get; set; }

    /// <summary>
    /// Custom permissions (null to keep existing, empty list to use role defaults)
    /// </summary>
    public List<string>? Permissions { get; set; }
}

/// <summary>
/// Request to update user permissions
/// </summary>
public class UpdatePermissionsRequest
{
    /// <summary>
    /// List of permission codes to grant
    /// </summary>
    [Required]
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// Whether to merge with existing or replace
    /// </summary>
    public bool ReplaceAll { get; set; } = true;
}

/// <summary>
/// User permissions response
/// </summary>
public class UserPermissionsResponse
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
    public List<string> EffectivePermissions { get; set; } = new();
}

/// <summary>
/// All available permissions grouped by category
/// </summary>
public class AvailablePermissionsResponse
{
    public Dictionary<string, List<PermissionDto>> PermissionsByCategory { get; set; } = new();
}

/// <summary>
/// Permission DTO
/// </summary>
public class PermissionDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

#endregion

#region User Activity DTOs

/// <summary>
/// User activity summary
/// </summary>
public class UserActivitySummary
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public int TotalActions { get; set; }
    public int ActionsToday { get; set; }
    public int ActionsThisWeek { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public string? LastAction { get; set; }
    public List<UserActivityItem> RecentActivities { get; set; } = new();
}

/// <summary>
/// Individual activity item
/// </summary>
public class UserActivityItem
{
    public int Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? PageUrl { get; set; }
    public bool IsSuccess { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// User activity dashboard data
/// </summary>
public class UserActivityDashboard
{
    public int TotalUsersActive { get; set; }
    public int TotalActionsToday { get; set; }
    public List<UserActivitySummary> MostActiveUsers { get; set; } = new();
    public List<ActionTypeCount> ActionBreakdown { get; set; } = new();
    public List<HourlyActivityCount> HourlyActivity { get; set; } = new();
}

/// <summary>
/// Action type count
/// </summary>
public class ActionTypeCount
{
    public string Action { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// Hourly activity count
/// </summary>
public class HourlyActivityCount
{
    public int Hour { get; set; }
    public int Count { get; set; }
}

#endregion
