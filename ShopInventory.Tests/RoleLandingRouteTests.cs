using System.Security.Claims;
using ShopInventory.Web.Common;
using ShopInventory.Web.Data;

namespace ShopInventory.Tests;

/// <summary>
/// Where each role lands when it signs in. The sign-in page, RedirectToLogin and
/// the dashboard all resolve through this, so a disagreement between them is a
/// failure here rather than a bounce the user sees.
/// </summary>
public sealed class RoleLandingRouteTests
{
    [Theory]
    [InlineData(UserRoles.PodOperator, "/pod-dashboard")]
    [InlineData(UserRoles.SalesRep, "/dashboard")]
    [InlineData(UserRoles.Admin, "/dashboard")]
    [InlineData(UserRoles.Cashier, "/cashier-dashboard")]
    // These two shared the dashboard until it narrowed to an administrator's
    // page, and land on the page they work from until each has its own.
    [InlineData(UserRoles.Manager, "/reports")]
    [InlineData(UserRoles.StockController, "/inventory-transfers")]
    [InlineData(UserRoles.Driver, "/pods")]
    [InlineData("Operator", "/pods")]
    [InlineData(UserRoles.Merchandiser, "/mobile-drafts")]
    [InlineData(UserRoles.DepotController, "/dashboard")]
    [InlineData(UserRoles.Lab, "/lab/batch-status")]
    [InlineData(UserRoles.MerchandiserPurchaseOrderViewer, "/reports/merchandiser-purchase-orders")]
    public void Each_role_lands_on_its_own_page(string role, string expected)
    {
        Assert.Equal(expected, RoleLandingRoutes.For(role));
        Assert.Equal(expected, RoleLandingRoutes.For(Principal(role)));
    }

    /// <summary>
    /// The operator runs a section and gets the dashboard that reports on it;
    /// the driver only uploads their own proof, so the POD list stays their
    /// landing page. These two used to share one.
    /// </summary>
    [Fact]
    public void Operator_and_driver_land_apart()
    {
        Assert.Equal(RoleLandingRoutes.PodDashboard, RoleLandingRoutes.For(UserRoles.PodOperator));
        Assert.Equal("/pods", RoleLandingRoutes.For(UserRoles.Driver));
    }

    /// <summary>
    /// Three roles resolve to the one dashboard route, which Home serves with a
    /// different page for each: the sales-rep workspace, the depot workspace and
    /// the administrator's one. Cashier is deliberately not among them any
    /// more — see <see cref="Each_role_lands_on_its_own_page"/>.
    /// </summary>
    [Theory]
    [InlineData(UserRoles.SalesRep)]
    [InlineData(UserRoles.DepotController)]
    [InlineData(UserRoles.Admin)]
    public void The_dashboard_route_is_shared(string role)
    {
        Assert.Equal(RoleLandingRoutes.Dashboard, RoleLandingRoutes.For(role));
    }

    /// <summary>
    /// An administrator carrying a narrower role keeps the dashboard, by any of
    /// the three routes to that answer: Lab is still checked against Admin
    /// explicitly, the depot role lands there on its own, and the re-homed
    /// three are resolved after Admin.
    /// </summary>
    [Theory]
    [InlineData(UserRoles.Lab)]
    [InlineData(UserRoles.DepotController)]
    [InlineData(UserRoles.Cashier)]
    [InlineData(UserRoles.StockController)]
    [InlineData(UserRoles.Manager)]
    public void An_admin_holding_a_narrow_role_still_lands_on_the_dashboard(string role)
    {
        Assert.Equal(RoleLandingRoutes.Dashboard, RoleLandingRoutes.For(Principal(UserRoles.Admin, role)));
    }

    /// <summary>
    /// A sales rep is resolved before the re-homed roles, so a rep who also
    /// carries one of them keeps their own workspace.
    /// </summary>
    [Theory]
    [InlineData(UserRoles.Cashier)]
    [InlineData(UserRoles.Manager)]
    public void A_sales_rep_holding_a_re_homed_role_keeps_the_dashboard(string role)
    {
        Assert.Equal(RoleLandingRoutes.Dashboard, RoleLandingRoutes.For(Principal(UserRoles.SalesRep, role)));
    }

    /// <summary>
    /// Every role the nav offers an Overview link to resolves to a page that
    /// role can actually open. The link's href comes from this chain, so a role
    /// listed there and missing here would send someone to a page their own
    /// authorize attribute refuses — which is the bug the hard-coded
    /// /dashboard href was.
    /// </summary>
    [Fact]
    public void Every_overview_nav_role_lands_on_a_dashboard()
    {
        var dashboards = new[] { RoleLandingRoutes.Dashboard, RoleLandingRoutes.CashierDashboard };

        foreach (var role in UserRoles.DashboardNavRoles.Split(','))
        {
            Assert.Contains(RoleLandingRoutes.For(role), dashboards);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SomeRoleNobodyDefined")]
    public void An_unknown_or_missing_role_falls_back(string? role)
    {
        Assert.Equal(RoleLandingRoutes.Fallback, RoleLandingRoutes.For(role));
    }

    [Fact]
    public void The_role_name_is_matched_regardless_of_case_or_padding()
    {
        Assert.Equal(RoleLandingRoutes.PodDashboard, RoleLandingRoutes.For(" podoperator "));
    }

    [Fact]
    public void An_anonymous_principal_falls_back()
    {
        Assert.Equal(RoleLandingRoutes.Fallback, RoleLandingRoutes.For((ClaimsPrincipal?)null));
        Assert.Equal(RoleLandingRoutes.Fallback, RoleLandingRoutes.For(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private static ClaimsPrincipal Principal(params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(role => new Claim(ClaimTypes.Role, role)),
            authenticationType: "Test",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role));
}
