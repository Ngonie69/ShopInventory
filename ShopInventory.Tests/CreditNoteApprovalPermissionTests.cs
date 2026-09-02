using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The permission family for deciding and adding A/R credit memos that SAP's own approval procedure is
/// holding, pinned against the role defaults it changes.
/// </summary>
/// <remarks>
/// The family is separate from <c>invoices.*</c> on purpose. A cashier raises credit notes; a manager
/// decides another person's and posts it. If the two ever merged, granting a cashier invoicing would also
/// hand them approval of the credit memos they raise — the separation SAP's approval procedure exists to
/// enforce.
/// </remarks>
public sealed class CreditNoteApprovalPermissionTests
{
    [Fact]
    public void Both_permissions_are_in_the_catalogue()
    {
        var all = Permissions.GetAllPermissions();
        Assert.Contains(Permissions.ApproveSapCreditNotes, all);
        Assert.Contains(Permissions.AddApprovedCreditNotes, all);

        var grouped = Permissions.GetAllPermissionsGrouped();
        Assert.True(grouped.TryGetValue("Credit Note Approvals", out var bucket));
        Assert.Equal(
            new[] { Permissions.ApproveSapCreditNotes, Permissions.AddApprovedCreditNotes },
            bucket!.Select(permission => permission.Code).ToArray());
    }

    [Theory]
    [InlineData(ApplicationRoles.Admin)]
    [InlineData(ApplicationRoles.Manager)]
    public void Managers_and_administrators_hold_both_by_default(string role)
    {
        var permissions = Permission.GetDefaultPermissionsForRole(role);

        Assert.Contains(Permission.ApproveSapCreditNotes, permissions);
        Assert.Contains(Permission.AddApprovedCreditNotes, permissions);
    }

    /// <summary>Raising a credit note is not deciding one.</summary>
    [Theory]
    [InlineData(ApplicationRoles.Cashier)]
    [InlineData(ApplicationRoles.SalesRep)]
    [InlineData(ApplicationRoles.User)]
    public void Invoicing_roles_do_not_decide_credit_memos(string role)
    {
        var permissions = Permission.GetDefaultPermissionsForRole(role);

        Assert.DoesNotContain(Permission.ApproveSapCreditNotes, permissions);
        Assert.DoesNotContain(Permission.AddApprovedCreditNotes, permissions);
    }

    [Fact]
    public void The_codes_are_their_own_family_not_the_invoice_one()
    {
        Assert.StartsWith("creditnotes.", Permissions.ApproveSapCreditNotes);
        Assert.StartsWith("creditnotes.", Permissions.AddApprovedCreditNotes);
    }
}
