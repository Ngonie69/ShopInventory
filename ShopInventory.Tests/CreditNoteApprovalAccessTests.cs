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
    /// into a 403.
    /// </summary>
    [Fact]
    public void Every_role_that_may_open_the_page_holds_both_permissions_by_default()
    {
        var roles = UserRoles.CreditNoteApprovalRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(roles);

        foreach (var role in roles)
        {
            var permissions = Permission.GetDefaultPermissionsForRole(role);
            Assert.Contains(Permission.ApproveSapCreditNotes, permissions);
            Assert.Contains(Permission.AddApprovedCreditNotes, permissions);
        }
    }

    private static MethodInfo[] Actions() => typeof(CreditNoteApprovalController)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => !method.IsSpecialName)
        .ToArray();

    private static string[] RequiredPermissions(MethodInfo action) => action
        .GetCustomAttributes<RequirePermissionAttribute>(inherit: true)
        .SelectMany(attribute => attribute.RequiredPermissions)
        .ToArray();
}
