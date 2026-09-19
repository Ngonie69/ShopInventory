using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using ShopInventory.Authentication;
using ShopInventory.Controllers;
using ShopInventory.Models;
using ShopInventory.Web.Data;

namespace ShopInventory.Tests;

/// <summary>
/// The Web page is gated by role and the API by permission, and neither can see the other. These pin
/// the two together: a page role without the permission is a Confirm button that submits into a 403,
/// and a permission holder left off the page never finds the queue.
/// </summary>
public sealed class MarketBreakageAccessTests
{
    [Fact]
    public void Every_role_that_may_open_the_page_may_confirm()
    {
        var roles = Split(UserRoles.MarketBreakageRoles);
        Assert.NotEmpty(roles);

        foreach (var role in roles)
            Assert.Contains(Permission.ConfirmMarketBreakages, Permission.GetDefaultPermissionsForRole(role));
    }

    [Fact]
    public void Every_role_that_may_confirm_is_offered_the_page()
    {
        var pageRoles = Split(UserRoles.MarketBreakageRoles);
        var confirming = typeof(ApplicationRoles)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Select(field => (string)field.GetRawConstantValue()!)
            // ApiUser is the integration key's role, never a person signed in to the Web.
            .Where(role => role != ApplicationRoles.ApiUser)
            .Where(role => Permission.GetDefaultPermissionsForRole(role).Contains(Permission.ConfirmMarketBreakages))
            .ToArray();

        Assert.NotEmpty(confirming);
        foreach (var role in confirming)
            Assert.Contains(role, pageRoles);
    }

    /// <summary>
    /// The handset routes sit on the van sales controller, behind the ApiAccess policy that admits every
    /// staff role; an action without its permission would let any account file breakages as a van.
    /// </summary>
    [Fact]
    public void The_handset_routes_demand_the_report_permission()
    {
        var actions = typeof(VanSalesCompatibilityController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>()
                .Any(attribute => attribute.Template == "breakages"))
            .ToArray();

        Assert.Equal(2, actions.Length);
        foreach (var action in actions)
        {
            var required = action.GetCustomAttribute<RequirePermissionAttribute>()?.RequiredPermissions ?? [];
            Assert.Equal([Permission.ReportMarketBreakages], required);
        }
    }

    private static string[] Split(string roles)
        => roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
