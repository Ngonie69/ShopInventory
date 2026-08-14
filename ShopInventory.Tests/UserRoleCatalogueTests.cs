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
}
