using System.Text.Json;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The van handset signs in through its own compatibility route, whose reply is a different shape
/// from <c>/api/Auth/login</c>. It has to carry the password standing too, or the van app cannot
/// tell a rep their password has expired.
/// </summary>
public class VanSalesLoginPasswordStandingTests
{
    [Fact]
    public void The_van_login_reply_carries_the_password_standing_under_the_handsets_names()
    {
        var expiresAt = new DateTime(2027, 1, 13, 22, 0, 0, DateTimeKind.Utc);
        var auth = new AuthLoginResponse
        {
            AccessToken = "token",
            RefreshToken = "refresh",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            PasswordChangeRequired = true,
            PasswordChangeReason = "Expired",
            PasswordExpiresAt = expiresAt,
            PasswordExpiresInDays = 0,
            PasswordExpiringSoon = true
        };
        var user = new User { Id = Guid.NewGuid(), Username = "van01", PasswordHash = "x", Role = "Sales", IsActive = true };

        var reply = VanSalesCompatibilityMapper.MapLoginResponse(auth, user, []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(reply));
        var root = json.RootElement;

        Assert.True(root.GetProperty("password_change_required").GetBoolean());
        Assert.Equal("Expired", root.GetProperty("password_change_reason").GetString());
        Assert.Equal(expiresAt, root.GetProperty("password_expires_at").GetDateTime());
        Assert.Equal(0, root.GetProperty("password_expires_in_days").GetInt32());
        Assert.True(root.GetProperty("password_expiring_soon").GetBoolean());
    }

    [Fact]
    public void Nothing_owed_reads_as_nothing_owed()
    {
        var auth = new AuthLoginResponse { AccessToken = "token", ExpiresAt = DateTime.UtcNow.AddHours(1) };
        var user = new User { Id = Guid.NewGuid(), Username = "van01", PasswordHash = "x", Role = "Sales", IsActive = true };

        var reply = VanSalesCompatibilityMapper.MapLoginResponse(auth, user, []);

        Assert.False(reply.PasswordChangeRequired);
        Assert.Null(reply.PasswordChangeReason);
        Assert.False(reply.PasswordExpiringSoon);
    }
}
