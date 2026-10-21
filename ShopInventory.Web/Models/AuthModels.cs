namespace ShopInventory.Web.Models;

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginResponse
{
    public bool RequiresTwoFactor { get; set; }
    public string? TwoFactorToken { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public UserInfo? User { get; set; }

    // Where the account stands on password renewal. Mirrors AuthLoginResponse on the API.
    public bool PasswordChangeRequired { get; set; }
    public string? PasswordChangeReason { get; set; }
    public DateTime? PasswordExpiresAt { get; set; }
    public int? PasswordExpiresInDays { get; set; }
    public bool PasswordExpiringSoon { get; set; }
}

/// <summary>
/// What the portal remembers, between sign-in and the next token refresh, about whether this
/// account's password has to be renewed.
/// </summary>
/// <param name="ChangeRequired">A new password is owed now; nothing else is reachable until it is chosen.</param>
/// <param name="Reason"><c>Expired</c> or <c>Required</c> when a change is owed.</param>
/// <param name="ExpiresAt">The renewal date in UTC, or null for an account whose password does not age.</param>
/// <param name="ExpiresInDays">Whole days until then, rounded up.</param>
/// <param name="ExpiringSoon">Close enough to the date to start reminding.</param>
public sealed record PasswordStandingInfo(
    bool ChangeRequired,
    string? Reason,
    DateTime? ExpiresAt,
    int? ExpiresInDays,
    bool ExpiringSoon)
{
    public const string Expired = "Expired";

    public static PasswordStandingInfo From(LoginResponse response) => new(
        response.PasswordChangeRequired,
        response.PasswordChangeReason,
        response.PasswordExpiresAt,
        response.PasswordExpiresInDays,
        response.PasswordExpiringSoon);
}

public class UserInfo
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Roles held on top of <see cref="Role"/>, e.g. Cashier on a CartVendor. Each becomes another
    /// role claim, so pages gated on either role open. <see cref="Role"/> stays first.
    /// </summary>
    public List<string> AdditionalRoles { get; set; } = new();

    public string? Email { get; set; }
    public string? AssignedWarehouseCode { get; set; }
    public List<string> AssignedWarehouseCodes { get; set; } = new();
    public string? AssignedBusinessPartnerCode { get; set; }
    public string? AssignedCostCentreCode { get; set; }
}

public class PasskeyOptionsResponse
{
    public string SessionToken { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = string.Empty;
}

public class PasskeyCredentialInfo
{
    public Guid Id { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public class PasskeyBrowserContext
{
    public string Origin { get; set; } = string.Empty;
    public string RpId { get; set; } = string.Empty;
}

public class RefreshTokenRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}

public class ErrorResponse
{
    public string Message { get; set; } = string.Empty;
    public List<string>? Errors { get; set; }
}

public class RegisterUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = "Cashier";
}
