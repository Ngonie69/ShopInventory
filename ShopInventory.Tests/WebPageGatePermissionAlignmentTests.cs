using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ShopInventory.Authentication;
using ShopInventory.Controllers;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Web pages a role can open, meeting the API permissions behind them.
/// </summary>
/// <remarks>
/// The Web gates pages by role and the API gates endpoints by permission. Until the API checked the
/// signed-in user on Web traffic, the Web's API key carried every request past the permission, so a
/// mismatch never showed. scripts/find_page_permission_gaps.py lists the mismatches. Each test here
/// pins one decision about a mismatch, by running the real filter against the role's defaults.
/// </remarks>
public sealed class WebPageGatePermissionAlignmentTests
{
    /// <summary>
    /// /mobile-drafts and /sales-orders offer a cashier "Post to SAP" on purpose.
    /// </summary>
    [Fact]
    public async Task A_cashier_can_post_an_approved_order_to_sap()
    {
        Assert.True(await Passes<SalesOrderController>(nameof(SalesOrderController.PostToSAP), ApplicationRoles.Cashier));
    }

    /// <summary>
    /// The /mobile-drafts order drawer loads the customer's uploaded purchase order for everyone who
    /// works the order.
    /// </summary>
    [Theory]
    [InlineData(ApplicationRoles.Cashier)]
    [InlineData(ApplicationRoles.SalesRep)]
    [InlineData(ApplicationRoles.Merchandiser)]
    public async Task Roles_working_mobile_orders_can_read_the_uploaded_purchase_order(string role)
    {
        Assert.True(await Passes<PurchaseOrderController>(nameof(PurchaseOrderController.GetDocuments), role));
    }

    /// <summary>
    /// The negative control for the widening above: a role with neither purchasing nor sales orders
    /// is still refused.
    /// </summary>
    [Theory]
    [InlineData(ApplicationRoles.Driver)]
    [InlineData(ApplicationRoles.PodOperator)]
    [InlineData(ApplicationRoles.DepotController)]
    public async Task Roles_without_orders_are_still_refused_the_purchase_order_files(string role)
    {
        Assert.False(await Passes<PurchaseOrderController>(nameof(PurchaseOrderController.GetDocuments), role));
    }

    /// <summary>
    /// /sales-orders shows the credit room before somebody approves. A sales rep approves orders but
    /// holds no customers.view.
    /// </summary>
    [Fact]
    public async Task A_sales_rep_who_approves_orders_can_see_credit_headroom()
    {
        Assert.Contains(Permission.ApproveSalesOrders, Permission.GetDefaultPermissionsForRole(ApplicationRoles.SalesRep));
        Assert.DoesNotContain(Permission.ViewCustomers, Permission.GetDefaultPermissionsForRole(ApplicationRoles.SalesRep));

        Assert.True(await Passes<CreditControlController>(nameof(CreditControlController.GetCreditHeadroom), ApplicationRoles.SalesRep));
    }

    [Theory]
    [InlineData(ApplicationRoles.Driver)]
    [InlineData(ApplicationRoles.TillOperator)]
    public async Task Roles_that_neither_approve_nor_view_customers_are_refused_credit_headroom(string role)
    {
        var defaults = Permission.GetDefaultPermissionsForRole(role);
        Assert.DoesNotContain(Permission.ViewCustomers, defaults);
        Assert.DoesNotContain(Permission.ApproveSalesOrders, defaults);

        Assert.False(await Passes<CreditControlController>(nameof(CreditControlController.GetCreditHeadroom), role));
    }

    /// <summary>
    /// /van-sales-customer-orders is where the depot roles load a van from what shops ordered. They
    /// hold the narrow fulfilment permission, not salesorders.*.
    /// </summary>
    [Theory]
    [InlineData(ApplicationRoles.StockController, nameof(VanSalesOrdersController.GetRouteLoad))]
    [InlineData(ApplicationRoles.StockController, nameof(VanSalesOrdersController.RecordDelivery))]
    [InlineData(ApplicationRoles.StockController, nameof(VanSalesOrdersController.Convert))]
    [InlineData(ApplicationRoles.DepotController, nameof(VanSalesOrdersController.GetRouteLoad))]
    [InlineData(ApplicationRoles.DepotController, nameof(VanSalesOrdersController.RecordDelivery))]
    [InlineData(ApplicationRoles.DepotController, nameof(VanSalesOrdersController.Convert))]
    public async Task The_depot_roles_can_fulfil_van_customer_orders(string role, string action)
    {
        Assert.True(await Passes<VanSalesOrdersController>(action, role));
    }

    /// <summary>The negative control: the fulfilment permission is not a way into sales orders.</summary>
    [Theory]
    [InlineData(ApplicationRoles.StockController)]
    [InlineData(ApplicationRoles.DepotController)]
    public async Task Fulfilling_van_orders_does_not_open_the_sales_order_api(string role)
    {
        Assert.False(await Passes<SalesOrderController>(nameof(SalesOrderController.Create), role));
    }

    [Fact]
    public async Task Every_role_the_customer_orders_page_admits_can_load_it()
    {
        foreach (var role in PageRoles("ShopInventory.Web.Components.Pages.VanSalesCustomerOrders"))
        {
            Assert.True(
                await Passes<VanSalesOrdersController>(nameof(VanSalesOrdersController.GetRouteLoad), role),
                $"{role} can open /van-sales-customer-orders but the API refuses its route load.");
        }
    }

    /// <summary>
    /// /van-sales/routes admits Admin and Manager. The route writes used to borrow users.edit, which would
    /// also let a manager edit every user account.
    /// </summary>
    [Theory]
    [InlineData(nameof(VanSalesReportController.CreateRoute))]
    [InlineData(nameof(VanSalesReportController.UpdateRoute))]
    [InlineData(nameof(VanSalesReportController.CreateRouteStop))]
    [InlineData(nameof(VanSalesReportController.UpdateRouteStop))]
    [InlineData(nameof(VanSalesReportController.DeleteRouteStop))]
    [InlineData(nameof(VanSalesReportController.ReorderRouteStops))]
    public async Task Every_role_the_routes_page_admits_can_edit_routes_and_stops(string action)
    {
        foreach (var role in PageRoles("ShopInventory.Web.Components.Pages.VanSalesRoutes"))
        {
            Assert.True(
                await Passes<VanSalesReportController>(action, role),
                $"{role} can open /van-sales/routes but the API refuses {action}.");
        }
    }

    /// <summary>The negative control: running the routes is not a way into user accounts.</summary>
    [Fact]
    public async Task Managing_routes_does_not_let_a_manager_edit_users()
    {
        Assert.False(await Passes<UserManagementController>(nameof(UserManagementController.UpdateUser), ApplicationRoles.Manager));
    }

    /// <summary>
    /// UpdateUserPermissionsAsync rejects a permission missing from the catalogue, so a permission left
    /// out of it could never be granted to one user.
    /// </summary>
    [Fact]
    public void The_van_sales_permissions_are_in_the_catalogue()
    {
        var all = Permission.GetAllPermissions();
        Assert.Contains(Permission.FulfilVanSalesCustomerOrders, all);
        Assert.Contains(Permission.ManageVanSalesRoutes, all);
    }

    /// <summary>The non-Admin roles a compiled Web page admits, read off its [Authorize] attribute.</summary>
    internal static string[] PageRoles(string pageTypeName)
    {
        var page = typeof(ShopInventory.Web.Data.UserRoles).Assembly.GetType(pageTypeName);
        Assert.NotNull(page);

        var authorize = page!.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(inherit: true).Single();
        var roles = authorize.Roles!
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(role => role != ApplicationRoles.Admin)
            .ToArray();

        Assert.NotEmpty(roles);
        return roles;
    }

    /// <summary>
    /// Whether every [RequirePermission] on the action lets a user with the role's default permissions
    /// through, answered by the filter itself.
    /// </summary>
    internal static async Task<bool> Passes<TController>(string actionName, string role)
    {
        var action = typeof(TController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == actionName);

        var attributes = typeof(TController).GetCustomAttributes<RequirePermissionAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<RequirePermissionAttribute>(inherit: true))
            .ToArray();
        Assert.NotEmpty(attributes);

        foreach (var attribute in attributes)
        {
            var context = FilterContextFor(role);
            await attribute.OnAuthorizationAsync(context);
            if (context.Result is not null)
            {
                return false;
            }
        }

        return true;
    }

    private static AuthorizationFilterContext FilterContextFor(string role)
    {
        var userId = Guid.NewGuid();
        var defaults = Permission.GetDefaultPermissionsForRole(role);

        var services = new ServiceCollection();
        services.AddSingleton(StubProxy.For<IUserManagementService>((method, args) =>
            method.Name == nameof(IUserManagementService.GetEffectivePermissionsAsync) && (Guid)args![0]! == userId
                ? Task.FromResult(defaults)
                : throw new InvalidOperationException($"IUserManagementService.{method.Name} was not expected to be called.")));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role)],
            authenticationType: "Bearer");

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = services.BuildServiceProvider()
        };

        return new AuthorizationFilterContext(new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), []);
    }
}
