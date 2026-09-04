using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Controllers;

namespace ShopInventory.Tests;

/// <summary>
/// <c>POST /api/RateLimit/unblock/{clientId}</c> still answers, as an alias for
/// <c>reset/{clientId}</c>.
///
/// The two were once separate endpoints that drifted: reset zeroed the request counter and left
/// <c>IsBlocked</c> set, so resetting a blocked client left it blocked. They are one action now, so
/// they cannot disagree - but unblock has to keep answering, because API.md's versioning policy is
/// that a version 1.0 endpoint stays working for the clients already calling it. Deleting it was
/// the wrong call and this is the guard against making it again.
/// </summary>
public sealed class RateLimitUnblockAliasTests
{
    private static readonly MethodInfo ResetAction =
        typeof(RateLimitController).GetMethod(nameof(RateLimitController.ResetClient))!;

    [Fact]
    public void Both_routes_are_served_by_one_action()
    {
        var templates = ResetAction
            .GetCustomAttributes<HttpPostAttribute>()
            .SelectMany(attribute => attribute.Template is null ? [] : new[] { attribute.Template })
            .ToList();

        Assert.Contains("reset/{clientId}", templates);
        Assert.Contains("unblock/{clientId}", templates);
    }

    [Fact]
    public void No_other_action_claims_the_unblock_route()
    {
        // A second action answering unblock is how the two drifted apart the first time.
        var claimants = typeof(RateLimitController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpPostAttribute>()
                .Any(attribute => attribute.Template == "unblock/{clientId}"))
            .ToList();

        Assert.Single(claimants);
        Assert.Equal(nameof(RateLimitController.ResetClient), claimants[0].Name);
    }

    [Fact]
    public void The_alias_carries_the_same_permission()
    {
        // Both templates hang off one action, so the permission cannot differ between them - this
        // asserts the action is gated at all, which is what makes that worth relying on.
        var permission = ResetAction
            .GetCustomAttributes()
            .Any(attribute => attribute.GetType().Name.StartsWith("RequirePermission"));

        Assert.True(permission, "ResetClient must stay permission-gated; both routes inherit it.");
    }
}
