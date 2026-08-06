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
    // These three shared the dashboard until it narrowed to an administrator's
    // page, and land on the page they work from until each has its own.
    [InlineData(UserRoles.Cashier, "/invoices")]
    [InlineData(UserRoles.Manager, "/reports")]
    [InlineData(UserRoles.StockController, "/inventory-transfers")]
    [InlineData(UserRoles.Driver, "/pods")]
    [InlineData("Operator", "/pods")]
    [InlineData(UserRoles.Merchandiser, "/mobile-drafts")]
    [InlineData(UserRoles.DepotController, "/inventory-transfers")]
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
    /// A sales rep resolves to the dashboard route, which Home serves with the
    /// sales-rep dashboard rather than the administrator's one.
    /// </summary>
    [Fact]
    public void Sales_rep_lands_on_the_dashboard_route()
    {
        Assert.Equal(RoleLandingRoutes.Dashboard, RoleLandingRoutes.For(UserRoles.SalesRep));
    }

    /// <summary>
    /// An administrator carrying a narrower role keeps the dashboard, whether
    /// that role is checked against Admin explicitly or resolved after it.
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
