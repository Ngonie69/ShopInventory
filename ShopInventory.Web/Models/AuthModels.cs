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
}

public class UserInfo
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? AssignedWarehouseCode { get; set; }
    public List<string> AssignedWarehouseCodes { get; set; } = new();
    public List<string> AllowedPaymentMethods { get; set; } = new();
    public string? DefaultGLAccount { get; set; }
    public List<string> AllowedPaymentBusinessPartners { get; set; } = new();
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
