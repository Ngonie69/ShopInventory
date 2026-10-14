using System.Text.Json.Serialization;

namespace ShopInventory.DTOs;

public class VanSalesLoginResponse
{
    [JsonPropertyName("user")]
    public VanSalesLoginUserDto User { get; set; } = null!;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("shop")]
    public List<VanSalesShopDto> Shop { get; set; } = new();

    [JsonPropertyName("type")]
    public string Type { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("rate")]
    public int Rate { get; set; }

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Where the account stands on the password policy — the same five members as
    /// <see cref="AuthLoginResponse"/>, named the handset's way. On the refresh reply too, so a van
    /// that stays signed in for weeks still learns that its renewal has come due.
    /// </summary>
    [JsonPropertyName("password_change_required")]
    public bool PasswordChangeRequired { get; set; }

    /// <summary><c>Expired</c> or <c>Required</c>; null when no change is owed.</summary>
    [JsonPropertyName("password_change_reason")]
    public string? PasswordChangeReason { get; set; }

    [JsonPropertyName("password_expires_at")]
    public DateTime? PasswordExpiresAt { get; set; }

    [JsonPropertyName("password_expires_in_days")]
    public int? PasswordExpiresInDays { get; set; }

    [JsonPropertyName("password_expiring_soon")]
    public bool PasswordExpiringSoon { get; set; }
}