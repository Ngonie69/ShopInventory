using System.Security.Claims;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Common;

/// <summary>
/// Where a signed-in user belongs when no particular page was asked for.
///
/// Three surfaces need this answer and used to hold their own copy of it: the
/// sign-in page after a successful login, <c>RedirectToLogin</c> when an
/// authenticated user reaches a page their role cannot open, and the dashboard
/// itself for the roles it does not serve. They disagreed — a POD operator was
/// sent to the POD list by one and the dashboard by another — so the chain
/// lives here once, ordered from the most specialised role to the least.
/// </summary>
public static class RoleLandingRoutes
{
    /// <summary>The route for roles with no landing page of their own.</summary>
    public const string Fallback = "/security";

    /// <summary>
    /// The dashboard route. It serves three different pages: Home renders the
    /// sales-rep dashboard for a SalesRep, the depot one for a DepotController
    /// and the administrator's one for an Admin, so each of those roles signing
    /// in arrives at their own workspace.
    /// </summary>
    public const string Dashboard = "/dashboard";

    public const string PodDashboard = "/pod-dashboard";

    /// <summary>
    /// Resolves the landing route from a role predicate, so a caller holding a
    /// <see cref="ClaimsPrincipal"/> and one holding only the role name from a
    /// login response both walk the same ordered chain.
    /// </summary>
    public static string For(Func<string, bool> isInRole)
    {
        ArgumentNullException.ThrowIfNull(isInRole);

        // Lab is checked against Admin because an administrator carrying it
        // should still land on the dashboard rather than in a single-purpose
        // workspace. A depot controller needs no such guard any more: they land
        // on the dashboard too, and Home decides which of the three it draws.
        if (isInRole(UserRoles.Lab) && !isInRole(UserRoles.Admin))
        {
            return "/lab/batch-status";
        }

        if (isInRole(UserRoles.MerchandiserPurchaseOrderViewer))
        {
            return "/reports/merchandiser-purchase-orders";
        }

        // The operator runs a section: they get the POD dashboard, which reports
        // on the whole section. A driver only uploads their own proof, so the
        // POD list is the page they actually work in.
        if (isInRole(UserRoles.PodOperator))
        {
            return PodDashboard;
        }

        if (isInRole(UserRoles.Driver) || isInRole("Operator"))
        {
            return "/pods";
        }

        if (isInRole(UserRoles.Merchandiser))
        {
            return "/mobile-drafts";
        }

        // The three roles the dashboard route still serves, each with a page of
        // its own that Home picks between.
        if (isInRole(UserRoles.Admin) ||
            isInRole(UserRoles.SalesRep) ||
            isInRole(UserRoles.DepotController))
        {
            return Dashboard;
        }

        // These three were served by the dashboard until it narrowed to an
        // administrator's page. Until each has a workspace of its own they land
        // on the page they actually work from — see docs/role-dashboards-plan.md.
        // Admin is resolved above, so an administrator carrying one of these
        // roles still reaches the dashboard.
        if (isInRole(UserRoles.Cashier))
        {
            return "/invoices";
        }

        if (isInRole(UserRoles.StockController))
        {
            return "/inventory-transfers";
        }

        if (isInRole(UserRoles.Manager))
        {
            return "/reports";
        }

        return Fallback;
    }

    public static string For(ClaimsPrincipal? user) =>
        user is null ? Fallback : For(user.IsInRole);

    /// <summary>
    /// Resolves from the single role string a login response carries. Accounts
    /// hold one role, so this is the whole of what the sign-in page knows.
    /// </summary>
    public static string For(string? role) =>
        string.IsNullOrWhiteSpace(role)
            ? Fallback
            : For(candidate => string.Equals(candidate, role.Trim(), StringComparison.OrdinalIgnoreCase));
}
