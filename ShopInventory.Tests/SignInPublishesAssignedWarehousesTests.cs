using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The user a sign-in publishes to the circuit carries the account's assigned warehouses.
/// </summary>
/// <remarks>
/// The wash bay and stock dashboards read the "warehouse" claims. Sign-in used to publish only the
/// name and role, so the page the wash bay lands on straight after signing in said "no warehouse
/// assigned" until it was reloaded (found driving /washbay-dashboard, 2026-11-09).
/// </remarks>
public sealed class SignInPublishesAssignedWarehousesTests
{
    [Fact]
    public async Task A_sign_in_publishes_the_warehouses_the_login_answer_carried()
    {
        var provider = Provider();
        AuthenticationState? published = null;
        provider.AuthenticationStateChanged += async task => published = await task;

        await provider.StoreAuthData(Login("washbay", "WashBay", ["FSRW", "DISP"]));
        provider.NotifyUserAuthentication("washbay", "WashBay");

        Assert.NotNull(published);
        Assert.True(published.User.IsInRole("WashBay"));
        Assert.Equal(["FSRW", "DISP"], published.User.FindAll("warehouse").Select(claim => claim.Value));
    }

    [Fact]
    public async Task A_name_the_stored_login_does_not_match_is_published_as_given()
    {
        var provider = Provider();
        AuthenticationState? published = null;
        provider.AuthenticationStateChanged += async task => published = await task;

        await provider.StoreAuthData(Login("someone.else", "StockController", ["FSRW"]));
        provider.NotifyUserAuthentication("washbay", "WashBay");

        Assert.NotNull(published);
        Assert.Equal("washbay", published.User.Identity?.Name);
        Assert.True(published.User.IsInRole("WashBay"));
        Assert.Empty(published.User.FindAll("warehouse"));
    }

    private static CustomAuthStateProvider Provider() => new(
        ExpiringSession.BrowserStorage(new Dictionary<string, object?>()),
        new HttpClient { BaseAddress = new Uri("http://localhost:5106/") },
        NullLogger<CustomAuthStateProvider>.Instance,
        new WebClientAuditContext());

    private static LoginResponse Login(string username, string role, List<string> warehouses) => new()
    {
        AccessToken = "token",
        RefreshToken = "refresh",
        ExpiresAt = DateTime.UtcNow.AddHours(1),
        User = new UserInfo { Username = username, Role = role, AssignedWarehouseCodes = warehouses }
    };
}
