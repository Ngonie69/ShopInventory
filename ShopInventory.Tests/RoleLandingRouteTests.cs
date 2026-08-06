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
    [InlineData(UserRoles.Cashier, "/dashboard")]
    [InlineData(UserRoles.Manager, "/dashboard")]
    [InlineData(UserRoles.StockController, "/dashboard")]
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
    /// the cashier dashboard everyone else gets.
    /// </summary>
    [Theory]
    [InlineData(UserRoles.SalesRep)]
    [InlineData(UserRoles.DepotController)]
    [InlineData(UserRoles.Cashier)]
    public void The_dashboard_route_is_shared(string role)
    {
        Assert.Equal(RoleLandingRoutes.Dashboard, RoleLandingRoutes.For(role));
    }

    /// <summary>
    /// An administrator carrying a single-purpose role keeps the dashboard. Lab
    /// is the one still checked against Admin; the depot role reaches the same
    /// answer on its own now that it lands there too.
    /// </summary>
    [Theory]
    [InlineData(UserRoles.Lab)]
    [InlineData(UserRoles.DepotController)]
    public void An_admin_holding_a_narrow_role_still_lands_on_the_dashboard(string role)
    {
        Assert.Equal(RoleLandingRoutes.Dashboard, RoleLandingRoutes.For(Principal(UserRoles.Admin, role)));
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
