namespace ShopInventory.DTOs;

/// <summary>
/// Login request DTO
/// </summary>
public class AuthLoginRequest
{
    /// <summary>
    /// Username or email
    /// </summary>
    public required string Username { get; set; }

    /// <summary>
    /// User password
    /// </summary>
    public required string Password { get; set; }
}

/// <summary>
/// Login response with tokens
/// </summary>
public class AuthLoginResponse
{
    /// <summary>
    /// When true, the client must call POST /api/auth/login/two-factor with the TwoFactorToken
    /// and the TOTP code. AccessToken and RefreshToken will be empty.
    /// </summary>
    public bool RequiresTwoFactor { get; set; }

    /// <summary>
    /// Opaque short-lived token (5 minutes) to be exchanged at the two-factor challenge endpoint.
    /// Only present when RequiresTwoFactor is true.
    /// </summary>
    public string? TwoFactorToken { get; set; }

    /// <summary>
    /// JWT access token
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Refresh token for obtaining new access tokens
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Access token expiration time in UTC
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Token type (always "Bearer")
    /// </summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>
    /// User information
    /// </summary>
    public UserInfo? User { get; set; }

    /// <summary>
    /// True when the account has to choose a new password before it does anything else.
    /// </summary>
    /// <remarks>
    /// The tokens beside it are real and the sign-in succeeded. A client that understands this
    /// takes the user straight to its change-password screen; one that does not carries on, and is
    /// held to the password endpoints by the API itself when
    /// <c>Security:PasswordPolicy:EnforceOnApiClients</c> is on.
    /// </remarks>
    public bool PasswordChangeRequired { get; set; }

    /// <summary>
    /// Why: <c>Expired</c> (older than the policy allows) or <c>Required</c> (set by somebody
    /// else, or flagged by an administrator). Null when no change is owed.
    /// </summary>
    public string? PasswordChangeReason { get; set; }

    /// <summary>
    /// When the password has to be renewed, in UTC. Null for an account whose password does not age.
    /// </summary>
    public DateTime? PasswordExpiresAt { get; set; }

    /// <summary>
    /// Whole days until then, rounded up; 0 once it is overdue. Null with <see cref="PasswordExpiresAt"/>.
    /// </summary>
    public int? PasswordExpiresInDays { get; set; }

    /// <summary>
    /// True once the renewal date is close enough to start reminding the user. The server decides
    /// what "close" is (<c>Security:PasswordPolicy:ExpiryWarningDays</c>), so no client keeps its
    /// own copy of the number.
    /// </summary>
    public bool PasswordExpiringSoon { get; set; }
}

/// <summary>
/// User information returned after login
/// </summary>
public class UserInfo
{
    public required string Username { get; set; }
    public required string Role { get; set; }
    public string? Email { get; set; }
    public string? AssignedWarehouseCode { get; set; }
    public List<string> AssignedWarehouseCodes { get; set; } = new();
    public string? AssignedSection { get; set; }
    public string? AssignedBusinessPartnerCode { get; set; }
    public string? AssignedCostCentreCode { get; set; }
    public List<string> AssignedCustomerCodes { get; set; } = new();

    /// <summary>
    /// The warehouse this account's stock requests are raised against — who it asks for stock.
    /// </summary>
    /// <remarks>
    /// The shop's supplying warehouse for an account on a shop, set under Administration › Shops;
    /// otherwise the account's own, which only a van has. The till shows it on the stock request screen
    /// and the platform applies the same value when the request arrives, so the till never chooses it.
    /// </remarks>
    public string? TransferSourceWarehouseCode { get; set; }

    /// <summary>
    /// The shop this account works the till at, when it has one.
    /// </summary>
    /// <remarks>
    /// Sent so the till stops choosing its own shop. That choice used to be a device preference picked
    /// at setup from a list compiled into the app, which decided what the till read stock and history
    /// for while the account decided what it sold from — two answers nothing reconciled.
    ///
    /// The three code fields above are populated from the shop for an account that has one, so a
    /// client reading them needs no knowledge of shops to get the right answer. <see cref="ShopCode"/>
    /// and <see cref="ShopName"/> are what a till shows the operator and what lets it skip the setup
    /// screen.
    /// </remarks>
    public string? ShopCode { get; set; }

    /// <inheritdoc cref="ShopCode"/>
    public string? ShopName { get; set; }
}

/// <summary>
/// Refresh token request
/// </summary>
public class RefreshTokenRequest
{
    /// <summary>
    /// The refresh token to exchange for new tokens
    /// </summary>
    public required string RefreshToken { get; set; }
}

/// <summary>
/// A signed-in user changing their own password, as the merchandiser app sends it.
/// </summary>
/// <remarks>
/// The same change as <c>POST /api/password/change</c> without the confirmation field: that app
/// compares the two boxes itself and sends the password once.
/// </remarks>
public class OwnPasswordChangeRequest
{
    public string CurrentPassword { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// API Key authentication request
/// </summary>
public class ApiKeyAuthRequest
{
    /// <summary>
    /// The API key
    /// </summary>
    public required string ApiKey { get; set; }
}

/// <summary>
/// Register user request (Admin only)
/// </summary>
public class RegisterUserRequest
{
    /// <summary>
    /// Username for the new user
    /// </summary>
    public required string Username { get; set; }

    /// <summary>
    /// Email address for the new user
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Password for the new user
    /// </summary>
    public required string Password { get; set; }

    /// <summary>
    /// Role for the new user
    /// </summary>
    public required string Role { get; set; }
}
