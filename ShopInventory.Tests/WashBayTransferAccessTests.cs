using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using ShopInventory.Controllers;
using ShopInventory.Models;
using ShopInventory.Web.Data;

namespace ShopInventory.Tests;

/// <summary>
/// The wash bay holds a stock controller's transfer rights. Those rights are spread over role strings
/// on two controllers, a warehouse authorizer and the Web's page lists, none of which can see the
/// others — so the pairing is pinned over every action rather than the ones that existed when the role
/// was added.
/// </summary>
public sealed class WashBayTransferAccessTests
{
    public static TheoryData<Type> TransferControllers => new()
    {
        typeof(InventoryTransferController),
        typeof(DesktopIntegrationController)
    };

    /// <summary>
    /// An action a stock controller may call and the wash bay may not is a button the wash bay sees on
    /// the transfers page that submits into a 403.
    /// </summary>
    [Theory]
    [MemberData(nameof(TransferControllers))]
    public void Every_action_open_to_a_stock_controller_is_open_to_the_wash_bay(Type controller)
    {
        var gated = controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(action => (action, roles: Roles(action)))
            .Where(item => item.roles.Contains(ApplicationRoles.StockController))
            .ToList();

        Assert.NotEmpty(gated);
        foreach (var (action, roles) in gated)
        {
            Assert.True(
                roles.Contains(ApplicationRoles.WashBay),
                $"{controller.Name}.{action.Name} admits StockController but not WashBay.");
        }
    }

    [Fact]
    public void The_transfer_pages_admit_the_wash_bay()
    {
        Assert.Contains(UserRoles.WashBay, UserRoles.InventoryTransferRoles.Split(','));
        Assert.True(UserRoles.CanViewInventoryTransfers(UserRoles.WashBay));
    }

    [Fact]
    public void The_wash_bay_holds_the_transfer_permissions_and_needs_no_warehouse()
    {
        var permissions = Permission.GetDefaultPermissionsForRole(ApplicationRoles.WashBay);

        Assert.Contains(Permission.TransferStock, permissions);
        Assert.Contains(Permission.TransferInventory, permissions);
        Assert.False(ApplicationRoles.RequiresWarehouseAssignments(ApplicationRoles.WashBay));
    }

    private static HashSet<string> Roles(MethodInfo action) => action
        .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
        .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Roles))
        .SelectMany(attribute => attribute.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToHashSet(StringComparer.Ordinal);
}
