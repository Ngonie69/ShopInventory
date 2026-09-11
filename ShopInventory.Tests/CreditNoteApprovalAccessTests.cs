using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Controllers;
using ShopInventory.Models;
using ShopInventory.Web.Data;

namespace ShopInventory.Tests;

/// <summary>
/// Who may reach the SAP credit memo approval routes, stated over every action rather than the ones
/// written first — the next action added is the one that gets copied from another controller with a
/// different permission attached.
/// </summary>
/// <remarks>
/// The controller sits behind the <c>ApiAccess</c> policy, which admits every staff role including the
/// handset ones. An action with no <c>[RequirePermission]</c> would let a driver decide a credit memo.
/// </remarks>
public sealed class CreditNoteApprovalAccessTests
{
    private static readonly string[] Family =
    [
        Permission.ApproveSapCreditNotes,
        Permission.AddApprovedCreditNotes
    ];

    [Fact]
    public void Every_endpoint_is_guarded_by_a_credit_note_approval_permission()
    {
        var actions = Actions();
        Assert.NotEmpty(actions);

        foreach (var action in actions)
        {
            var required = RequiredPermissions(action);

            Assert.True(
                required.Length > 0,
                $"{action.Name} carries no [RequirePermission], so it is open to every role in the ApiAccess policy.");

            var strays = required.Except(Family, StringComparer.Ordinal).ToArray();
            Assert.True(
                strays.Length == 0,
                $"{action.Name} is guarded by {string.Join(", ", strays)} rather than a creditnotes.* permission.");
        }
    }

    /// <summary>
    /// Someone who may only add still has to see the queue, and someone who may only decide still has
    /// to see what they are deciding; the reads take either.
    /// </summary>
    [Fact]
    public void Reads_accept_either_permission()
    {
        var reads = Actions().Where(action => action.GetCustomAttribute<HttpGetAttribute>() is not null).ToArray();
        Assert.NotEmpty(reads);

        foreach (var read in reads)
        {
            var required = RequiredPermissions(read);
            Assert.Contains(Permission.ApproveSapCreditNotes, required);
            Assert.Contains(Permission.AddApprovedCreditNotes, required);
        }
    }

    /// <summary>
    /// The cross-project pin. The page is gated by role and the API by permission, and neither side
    /// can see the other; a role on the page's list without the permission is a button that submits
    /// into a 403. Deciding and adding are pinned separately because the wash bay holds one and not
    /// the other.
    /// </summary>
    [Fact]
    public void Every_role_that_may_open_the_page_may_decide()
    {
        var roles = Split(UserRoles.CreditNoteApprovalRoles);
        Assert.NotEmpty(roles);

        foreach (var role in roles)
        {
            Assert.Contains(Permission.ApproveSapCreditNotes, Permission.GetDefaultPermissionsForRole(role));
        }
    }

    [Fact]
    public void Every_role_offered_the_add_may_add_and_may_open_the_page()
    {
        var roles = Split(UserRoles.CreditNoteAddRoles);
        Assert.NotEmpty(roles);

        foreach (var role in roles)
        {
            Assert.Contains(Permission.AddApprovedCreditNotes, Permission.GetDefaultPermissionsForRole(role));
            Assert.Contains(role, Split(UserRoles.CreditNoteApprovalRoles));
        }
    }

    /// <summary>
    /// The other direction, which the two pins above cannot see: a page role that holds the add but is
    /// left off the add list would lose a button it is entitled to, silently.
    /// </summary>
    [Fact]
    public void Every_page_role_that_holds_the_add_is_offered_it()
    {
        foreach (var role in Split(UserRoles.CreditNoteApprovalRoles))
        {
            var mayAdd = Permission.GetDefaultPermissionsForRole(role).Contains(Permission.AddApprovedCreditNotes);
            Assert.Equal(mayAdd, Split(UserRoles.CreditNoteAddRoles).Contains(role));
        }
    }

    [Fact]
    public void The_wash_bay_may_decide_but_not_add()
    {
        var permissions = Permission.GetDefaultPermissionsForRole(ApplicationRoles.WashBay);

        Assert.Contains(Permission.ApproveSapCreditNotes, permissions);
        Assert.DoesNotContain(Permission.AddApprovedCreditNotes, permissions);
        Assert.Contains(UserRoles.WashBay, Split(UserRoles.CreditNoteApprovalRoles));
        Assert.DoesNotContain(UserRoles.WashBay, Split(UserRoles.CreditNoteAddRoles));
    }

    private static string[] Split(string roles)
        => roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static MethodInfo[] Actions() => typeof(CreditNoteApprovalController)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => !method.IsSpecialName)
        .ToArray();

    private static string[] RequiredPermissions(MethodInfo action) => action
        .GetCustomAttributes<RequirePermissionAttribute>(inherit: true)
        .SelectMany(attribute => attribute.RequiredPermissions)
        .ToArray();
}
