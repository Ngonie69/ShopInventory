using System.Reflection;
using ShopInventory.Authentication;
using ShopInventory.Controllers;
using ShopInventory.Models;
using ShopInventory.Web.Data;

namespace ShopInventory.Tests;

/// <summary>
/// The Web page is gated by role and the API by permission, and neither can see the other. These pin
/// the two together, and pin that every factory route demands a factory permission rather than
/// resting on the ApiAccess policy that admits every staff role.
/// </summary>
public sealed class FactoryTransferAccessTests
{
    [Fact]
    public void Every_role_that_may_open_the_page_may_view_factory_transfers()
    {
        var roles = Split(UserRoles.FactoryTransferRoles);
        Assert.NotEmpty(roles);

        foreach (var role in roles)
            Assert.Contains(Permission.ViewFactoryTransfers, Permission.GetDefaultPermissionsForRole(role));
    }

    [Fact]
    public void Every_role_that_may_view_factory_transfers_is_offered_the_page()
    {
        var pageRoles = Split(UserRoles.FactoryTransferRoles);
        var viewing = typeof(ApplicationRoles)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Select(field => (string)field.GetRawConstantValue()!)
            // ApiUser is the integration key's role, never a person signed in to the Web.
            .Where(role => role != ApplicationRoles.ApiUser)
            .Where(role => Permission.GetDefaultPermissionsForRole(role).Contains(Permission.ViewFactoryTransfers))
            .ToArray();

        Assert.NotEmpty(viewing);
        foreach (var role in viewing)
            Assert.Contains(role, pageRoles);
    }

    [Fact]
    public void Both_factory_roles_must_name_their_warehouses()
    {
        Assert.True(ApplicationRoles.RequiresWarehouseAssignments(ApplicationRoles.ProductionOfficer));
        Assert.True(ApplicationRoles.RequiresWarehouseAssignments(ApplicationRoles.DispatchOfficer));
        Assert.True(UserRoles.IsFactoryRole(UserRoles.ProductionOfficer));
        Assert.True(UserRoles.IsFactoryRole(UserRoles.DispatchOfficer));
    }

    [Fact]
    public void Only_the_dispatch_manager_permission_accepts_for_any_department()
    {
        // Granted on the account, never by a factory role's defaults.
        Assert.DoesNotContain(Permission.AcceptAnyFactoryTransfers, Permission.GetDefaultPermissionsForRole(ApplicationRoles.ProductionOfficer));
        Assert.DoesNotContain(Permission.AcceptAnyFactoryTransfers, Permission.GetDefaultPermissionsForRole(ApplicationRoles.DispatchOfficer));
    }

    [Fact]
    public void Every_factory_route_demands_a_factory_permission()
    {
        var actions = typeof(FactoryTransferController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToArray();

        Assert.NotEmpty(actions);
        foreach (var action in actions)
        {
            var demanded = action.GetCustomAttributes<RequirePermissionAttribute>()
                .SelectMany(attribute => attribute.RequiredPermissions)
                .ToArray();
            Assert.True(demanded.Length > 0, $"{action.Name} has no permission");
            Assert.All(demanded, permission => Assert.StartsWith("factory.transfers.", permission));
        }
    }

    private static string[] Split(string roles)
        => roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
