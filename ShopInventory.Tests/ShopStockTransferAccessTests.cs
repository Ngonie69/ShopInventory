using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using ShopInventory.Controllers;
using ShopInventory.Models;
using ShopInventory.Web.Common;
using ShopInventory.Web.Data;

namespace ShopInventory.Tests;

/// <summary>
/// A shop's stock user (ShopStock) holds a depot controller's transfer rights and none of the depot's
/// other work. It is a separate role from the stock officer (StockController) because the factory
/// shop's stock user and the stock officers are different people.
/// </summary>
/// <remarks>
/// The transfer gates are role strings on three controllers and the Web's page lists, none of which
/// can see the others, so the pairing with the depot controller is pinned over every action rather
/// than the ones that existed when the role was added. <c>scripts/Roles/add_role_to_transfer_gates.py</c>
/// is what wrote them.
/// </remarks>
public sealed class ShopStockTransferAccessTests
{
    public static TheoryData<Type> TransferControllers => new()
    {
        typeof(InventoryTransferController),
        typeof(ApprovalProcessController)
    };

    /// <summary>
    /// An action a depot controller may call and the shop's stock user may not is a button on the
    /// transfers page that submits into a 403.
    /// </summary>
    [Theory]
    [MemberData(nameof(TransferControllers))]
    public void Every_transfer_action_open_to_a_depot_controller_is_open_to_the_shops_stock_user(Type controller)
    {
        var gated = Actions(controller)
            .Where(item => item.Roles.Contains(ApplicationRoles.DepotController))
            .ToList();

        Assert.NotEmpty(gated);
        foreach (var (action, roles, _) in gated)
        {
            Assert.True(
                roles.Contains(ApplicationRoles.ShopStock),
                $"{controller.Name}.{action.Name} admits DepotController but not ShopStock.");
        }
    }

    /// <summary>
    /// Desktop integration carries the SAP transfer request convert and close, which the transfers page
    /// calls, beside stock fetches the shop's stock user has no part in.
    /// </summary>
    [Fact]
    public void Desktop_integration_admits_the_shops_stock_user_to_transfer_requests_only()
    {
        var actions = Actions(typeof(DesktopIntegrationController))
            .Where(item => item.Roles.Contains(ApplicationRoles.DepotController))
            .ToList();

        var transferRequests = actions.Where(item => item.Route.StartsWith("transfer-requests/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, transferRequests.Count);
        Assert.All(transferRequests, item => Assert.Contains(ApplicationRoles.ShopStock, item.Roles));
        Assert.All(actions.Except(transferRequests), item => Assert.DoesNotContain(ApplicationRoles.ShopStock, item.Roles));
    }

    [Fact]
    public void The_shops_stock_user_is_a_separate_assignable_role_scoped_by_warehouse()
    {
        Assert.Contains(ApplicationRoles.ShopStock, ApplicationRoles.AssignableRoles);
        Assert.Contains(ApplicationRoles.ShopStock, ApplicationRoles.ApiAccessRoles);
        Assert.NotEqual(ApplicationRoles.StockController, ApplicationRoles.ShopStock);
        Assert.True(ApplicationRoles.RequiresWarehouseAssignments(ApplicationRoles.ShopStock));
        Assert.True(UserRoles.RequiresWarehouseAssignment(UserRoles.ShopStock));
    }

    [Fact]
    public void The_shops_stock_user_moves_stock_and_does_nothing_else_of_the_stock_office()
    {
        var permissions = Permission.GetDefaultPermissionsForRole(ApplicationRoles.ShopStock);

        Assert.Contains(Permission.TransferStock, permissions);
        Assert.Contains(Permission.TransferInventory, permissions);
        Assert.DoesNotContain(Permission.PostStockWriteOffs, permissions);
        Assert.DoesNotContain(Permission.ConfirmMarketBreakages, permissions);
        Assert.DoesNotContain(Permission.FulfilVanSalesCustomerOrders, permissions);
        Assert.DoesNotContain(Permission.ViewFactoryTransfers, permissions);
    }

    [Fact]
    public void The_shops_stock_user_opens_the_transfer_pages_and_lands_on_them()
    {
        Assert.Contains(UserRoles.ShopStock, UserRoles.InventoryTransferRoles.Split(','));
        Assert.Contains(UserRoles.ShopStock, UserRoles.InventoryTransferViewRoles.Split(','));
        Assert.True(UserRoles.CanViewInventoryTransfers(UserRoles.ShopStock));
        Assert.Equal(RoleLandingRoutes.InventoryTransfers, RoleLandingRoutes.For(UserRoles.ShopStock));
    }

    private static IEnumerable<(MethodInfo Action, HashSet<string> Roles, string Route)> Actions(Type controller)
        => controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(action => (action, Roles(action), Route(action)));

    private static HashSet<string> Roles(MethodInfo action) => action
        .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
        .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Roles))
        .SelectMany(attribute => attribute.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToHashSet(StringComparer.Ordinal);

    private static string Route(MethodInfo action) => action
        .GetCustomAttributes<HttpMethodAttribute>(inherit: true)
        .Select(attribute => attribute.Template ?? string.Empty)
        .FirstOrDefault() ?? string.Empty;
}
