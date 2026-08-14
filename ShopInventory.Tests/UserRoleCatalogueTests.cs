using System.Reflection;
using ShopInventory.Models;
using ShopInventory.Web.Data;

namespace ShopInventory.Tests;

/// <summary>
/// The Web's list of every role an account can hold, checked against the API's — the two
/// projects mirror each other by hand, and this is the pair where a difference is invisible
/// rather than loud.
/// </summary>
/// <remarks>
/// A page that addresses users by role — the mobile push composer is the one that shipped —
/// can only reach the roles its list names. Leave one out and the accounts on it are
/// unreachable, with no error to show for it: the send matches no devices and reports a
/// successful zero. So the list has to come from here, and here has to stay honest.
///
/// There are two catalogues, answering two questions, and conflating them breaks one or the
/// other. <c>AllRoles</c> is every role an account can be found <em>holding</em> and pairs with
/// <c>RetainableManagedRoles</c>; <c>AssignableRoles</c> is what an administrator may put on a
/// <em>new</em> account and pairs with <c>ApplicationRoles.AssignableRoles</c>. Operator, User
/// and ReadOnly are the difference. Both pairs are pinned below.
/// </remarks>
public sealed class UserRoleCatalogueTests
{
    /// <summary>
    /// Same set as the API's, which is the definition of what an account may be holding.
    /// </summary>
    [Fact]
    public void The_web_role_catalogue_matches_the_api_one()
    {
        Assert.Equal(
            ApplicationRoles.RetainableManagedRoles.OrderBy(role => role, StringComparer.OrdinalIgnoreCase),
            UserRoles.AllRoles.OrderBy(role => role, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The gap that prompted this: van sales work from a handset all day and are nowhere
    /// near a screen, so they are the group a push matters most to — and the two role names
    /// they are split across were the two missing from the composer.
    /// </summary>
    [Theory]
    [InlineData(UserRoles.Adr)]
    [InlineData(UserRoles.Sales)]
    [InlineData(UserRoles.Driver)]
    [InlineData(UserRoles.PodOperator)]
    [InlineData(UserRoles.Operator)]
    [InlineData(UserRoles.Merchandiser)]
    public void Every_role_that_carries_a_handset_can_be_targeted(string role)
    {
        Assert.Contains(role, UserRoles.AllRoles);
    }

    /// <summary>
    /// Every role name declared on <see cref="UserRoles"/> is in the catalogue, so adding a
    /// constant and forgetting the list fails here rather than in the field. The policy
    /// constants alongside them hold comma-separated lists for <c>[Authorize(Roles = …)]</c>
    /// and are skipped on that.
    /// </summary>
    [Fact]
    public void Every_declared_role_name_is_in_the_catalogue()
    {
        var declaredRoles = typeof(UserRoles)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => !value.Contains(','))
            .ToArray();

        Assert.NotEmpty(declaredRoles);
        foreach (var role in declaredRoles)
        {
            Assert.Contains(role, UserRoles.AllRoles);
        }
    }

    /// <summary>
    /// The role name is matched exactly when devices are looked up — <c>User.Role == role</c>
    /// against Postgres, which is case-sensitive — so a catalogue entry that differs from the
    /// stored spelling only in case would reach nobody.
    /// </summary>
    [Fact]
    public void The_catalogue_spells_each_role_exactly_once()
    {
        Assert.Equal(
            UserRoles.AllRoles.Count,
            UserRoles.AllRoles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The user management page reads the roles it offers from GET /api/user/roles, which serves
    /// <see cref="ApplicationRoles.AssignableRoles"/>, and falls back to
    /// <see cref="UserRoles.AssignableRoles"/> only when that call fails. The fallback is the copy
    /// that can rot unnoticed — it is only reached on a bad day — so it is pinned here.
    /// </summary>
    [Fact]
    public void The_web_assignable_role_list_matches_the_api()
    {
        Assert.Equal(
            ApplicationRoles.AssignableRoles.OrderBy(role => role, StringComparer.OrdinalIgnoreCase),
            UserRoles.AssignableRoles.OrderBy(role => role, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The two lists had drifted in opposite directions at once. The page offered ADR and Sales,
    /// which every create path refused with "Invalid role", so a new van sales account could not be
    /// opened while editing an existing one succeeded; and it never offered Manager, which the API
    /// accepts. ADR and Sales sit among the legacy constants because of the route-customer scope
    /// they use, not because the roles are closed — van sales is live and new vans need accounts.
    /// </summary>
    [Theory]
    [InlineData(UserRoles.Adr)]
    [InlineData(UserRoles.Sales)]
    [InlineData(UserRoles.Manager)]
    public void Roles_the_page_and_the_api_disagreed_over_are_assignable(string role)
    {
        Assert.True(ApplicationRoles.IsAssignableRole(role));
        Assert.Contains(role, UserRoles.AssignableRoles);
    }

    /// <summary>
    /// The three roles that separate the two catalogues: still held by existing accounts and still
    /// addressable by a push, but no longer handed out. Operator is deliberate — it stays
    /// runtime-supported until its management UX is normalized — and naming them here stops the
    /// gap between the lists being closed in the wrong direction.
    /// </summary>
    [Theory]
    [InlineData(UserRoles.Operator)]
    [InlineData(UserRoles.User)]
    [InlineData(UserRoles.ReadOnly)]
    public void Roles_no_longer_handed_out_are_still_addressable(string role)
    {
        Assert.False(ApplicationRoles.IsAssignableRole(role));
        Assert.True(ApplicationRoles.IsRetainableManagedRole(role));
        Assert.Contains(role, UserRoles.AllRoles);
        Assert.DoesNotContain(role, UserRoles.AssignableRoles);
    }

    /// <summary>
    /// A role that can be given to a new account has to survive an edit of that account, or the
    /// user would be created and then be unsaveable — and has to be addressable afterwards, or the
    /// account it opened could never be sent a push.
    /// </summary>
    [Fact]
    public void Every_assignable_role_is_retainable_and_addressable()
    {
        foreach (var role in ApplicationRoles.AssignableRoles)
        {
            Assert.True(ApplicationRoles.IsRetainableManagedRole(role), role);
            Assert.True(ApplicationRoles.CanAssignOrRetainManagedRole(role, currentRole: null), role);
            Assert.Contains(role, UserRoles.AllRoles);
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
