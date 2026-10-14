using ShopInventory.Web.Components.Layout;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Covers what the portal says about a password that is due, and which page it lets through.
/// </summary>
public sealed class PasswordRenewalTextTests
{
    [Theory]
    [InlineData("security")]
    [InlineData("security?renew=1")]
    [InlineData("Security")]
    [InlineData("/security/")]
    [InlineData("security#password")]
    public void The_security_page_is_the_one_page_an_account_owing_a_change_may_stay_on(string path)
    {
        Assert.True(PasswordRenewalText.IsChangePage(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dashboard")]
    [InlineData("security-settings")]
    [InlineData("user-management")]
    [InlineData("reports/security")]
    public void Every_other_page_sends_it_back(string path)
    {
        Assert.False(PasswordRenewalText.IsChangePage(path));
    }

    [Theory]
    [InlineData("security?renew=1")]
    [InlineData("login")]
    [InlineData("login?returnUrl=%2Fdashboard")]
    public void An_account_owing_a_change_is_left_alone_on_security_and_on_its_way_to_sign_in(string path)
    {
        // Sign-in is where a changed password sends the browser; dragging it back from there asked
        // for the change that had just been made.
        Assert.True(PasswordRenewalText.MayStayOn(path));
    }

    [Theory]
    [InlineData("dashboard")]
    [InlineData("login-history")]
    [InlineData("")]
    public void And_is_sent_back_from_anywhere_else(string path)
    {
        Assert.False(PasswordRenewalText.MayStayOn(path));
    }

    [Fact]
    public void The_change_page_link_is_itself_a_change_page()
    {
        // Otherwise the redirect would chase its own tail.
        Assert.True(PasswordRenewalText.IsChangePage(PasswordRenewalText.ChangePage));
    }

    [Fact]
    public void An_expired_password_is_called_expired()
    {
        var standing = new PasswordStandingInfo(true, PasswordStandingInfo.Expired, DateTime.UtcNow.AddDays(-2), 0, false);

        Assert.Equal("Your password has expired", PasswordRenewalText.Headline(standing));
        Assert.Contains("every three months", PasswordRenewalText.Detail(standing));
    }

    [Fact]
    public void A_password_somebody_else_set_is_not_called_expired()
    {
        var standing = new PasswordStandingInfo(true, "Required", DateTime.UtcNow.AddDays(80), 80, false);

        Assert.Equal("Choose a new password to carry on", PasswordRenewalText.Headline(standing));
        Assert.Contains("set for you", PasswordRenewalText.Detail(standing));
    }

    [Theory]
    [InlineData(14, "Your password expires in 14 days")]
    [InlineData(2, "Your password expires in 2 days")]
    // The count is rounded up to the midnight that ends the last good day, so 1 means today.
    [InlineData(1, "Your password expires today")]
    public void The_reminder_counts_down_in_days(int days, string expected)
    {
        var standing = new PasswordStandingInfo(false, null, DateTime.UtcNow.AddDays(days), days, true);

        Assert.Equal(expected, PasswordRenewalText.Headline(standing));
    }

    [Fact]
    public void The_portal_judges_passwords_with_the_same_policy_as_the_api()
    {
        // One source file compiled into both; this fails if the Web's copy ever stops being it.
        string[] samples = ["password", "Welcome2026", "river-stone-lantern", "12345678", "K3fal0s!", "short"];

        foreach (var sample in samples)
        {
            Assert.Equal(
                ShopInventory.Common.Security.PasswordPolicy.Check(sample),
                ShopInventory.Web.Common.Security.PasswordPolicy.Check(sample));
        }
    }
}
