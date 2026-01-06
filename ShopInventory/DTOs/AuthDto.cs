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
    /// JWT access token
    /// </summary>
    public required string AccessToken { get; set; }

    /// <summary>
    /// Refresh token for obtaining new access tokens
    /// </summary>
    public required string RefreshToken { get; set; }

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
}

/// <summary>
/// User information returned after login
/// </summary>
public class UserInfo
{
    public required string Username { get; set; }
    public required string Role { get; set; }
    public string? Email { get; set; }
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
    /// Role for the new user (Admin, Manager, User)
    /// </summary>
    public required string Role { get; set; }
}
