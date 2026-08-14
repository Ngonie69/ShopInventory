using ShopInventory.Models;
using ShopInventory.Web.Data;

namespace ShopInventory.Tests;

/// <summary>
/// The role catalogue exists twice: <see cref="ApplicationRoles"/> in the API, which every create
/// path validates against, and <see cref="UserRoles"/> in the Blazor app, which fills the role
/// picker on /user-management. The Web project takes no reference on the API project, so the two
/// cannot share an array and nothing but these tests holds them together.
///
/// They had drifted in both directions at once. The page offered ADR and Sales, which no create
/// path would accept, so opening a van sales account failed with "Invalid role" while editing an
/// existing one succeeded; and the page never offered Manager, which the API accepts, so no
/// manager could be created from it either.
/// </summary>
public sealed class UserRoleCatalogueTests
{
    /// <summary>
    /// The page reads the roles it offers from GET /api/user/roles, which serves
    /// <see cref="ApplicationRoles.AssignableRoles"/>, and falls back to
    /// <see cref="UserRoles.AllRoles"/> only when that call fails. The fallback is the copy that
    /// can rot unnoticed — a stale one is only reached on a bad day — so it is pinned here.
    /// </summary>
    [Fact]
    public void The_web_fallback_role_list_matches_the_api()
    {
        Assert.Equal(
            ApplicationRoles.AssignableRoles.OrderBy(role => role, StringComparer.Ordinal),
            UserRoles.AllRoles.OrderBy(role => role, StringComparer.Ordinal));
    }

    /// <summary>
    /// Van sales is a live workflow and new vans need accounts. ADR and Sales sit among the legacy
    /// constants because of the route-customer scope they use, not because they are closed, and
    /// leaving them out of the assignable list is what broke creation.
    /// </summary>
    [Theory]
    [InlineData(ApplicationRoles.Adr)]
    [InlineData(ApplicationRoles.Sales)]
    [InlineData(ApplicationRoles.Manager)]
    public void Roles_the_page_and_the_api_disagreed_over_are_assignable(string role)
    {
        Assert.True(ApplicationRoles.IsAssignableRole(role));
        Assert.Contains(role, UserRoles.AllRoles);
    }

    /// <summary>
    /// Operator is deliberately assignable-by-neither: it stays runtime-supported and retainable
    /// until its management UX is normalized. It is named here so that widening the assignable list
    /// for the van roles cannot be read as licence to sweep it in too.
    /// </summary>
    [Fact]
    public void Operator_remains_retainable_but_not_assignable()
    {
        Assert.False(ApplicationRoles.IsAssignableRole(ApplicationRoles.Operator));
        Assert.True(ApplicationRoles.IsRetainableManagedRole(ApplicationRoles.Operator));
        Assert.DoesNotContain(ApplicationRoles.Operator, UserRoles.AllRoles);
    }

    /// <summary>
    /// A role that can be given to a new account has to survive an edit of that account, or the
    /// user would be created and then be unsaveable.
    /// </summary>
    [Fact]
    public void Every_assignable_role_is_retainable()
    {
        foreach (var role in ApplicationRoles.AssignableRoles)
        {
            Assert.True(ApplicationRoles.IsRetainableManagedRole(role), role);
            Assert.True(ApplicationRoles.CanAssignOrRetainManagedRole(role, currentRole: null), role);
        }
    }

    /// <summary>
    /// Catches a role added to the assignable list without a permission set of its own: the switch
    /// in <c>GetDefaultPermissionsForRole</c> falls through to ViewDashboard alone, so an account
    /// created with it would open to an empty application. No real role's defaults are that single
    /// permission, which is what makes it usable as the tell.
    /// </summary>
    [Fact]
    public void Every_assignable_role_has_default_permissions_of_its_own()
    {
        foreach (var role in ApplicationRoles.AssignableRoles)
        {
            var permissions = Permission.GetDefaultPermissionsForRole(role);

            Assert.NotEmpty(permissions);
            Assert.False(
                permissions.Count == 1 && permissions[0] == Permission.ViewDashboard,
                $"{role} has no permission defaults of its own and fell through to the switch default.");
        }
    }

    /// <summary>
    /// A van account is defined by a business partner, a cost centre and a supplying warehouse.
    /// Only /api/usermanagement carries all three, so the other create paths refuse these two roles
    /// rather than opening a van rep with no route-customer scope and no depot to load from. That
    /// refusal is keyed on <c>UsesLegacyRouteCustomerScope</c>, so it must name the van roles and
    /// nothing else — a third role picked up here would become uncreatable everywhere but one
    /// endpoint, which is the bug this whole pairing exists to prevent.
    /// </summary>
    [Fact]
    public void Only_the_van_roles_carry_the_van_sales_assignments()
    {
        var vanRoles = ApplicationRoles.AssignableRoles
            .Where(ApplicationRoles.UsesLegacyRouteCustomerScope)
            .OrderBy(role => role, StringComparer.Ordinal);

        Assert.Equal(new[] { ApplicationRoles.Adr, ApplicationRoles.Sales }, vanRoles);

        foreach (var role in new[] { ApplicationRoles.Adr, ApplicationRoles.Sales })
        {
            Assert.True(ApplicationRoles.RequiresAssignedBusinessPartnerCode(role), role);
            Assert.True(ApplicationRoles.RequiresAssignedCostCentreCode(role), role);
            Assert.True(ApplicationRoles.RequiresSupplyingWarehouseCode(role), role);
            Assert.True(ApplicationRoles.RequiresWarehouseAssignments(role), role);
        }
    }
}
