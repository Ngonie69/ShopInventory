using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Models;

namespace ShopInventory.Features.SapTransferApprovals;

/// <summary>
/// Whether a pending approval request on a held transfer can be decided from here, and if not, why. The
/// drawer and the decision itself both ask this, so a button is never offered for a decision the API
/// would refuse.
/// </summary>
/// <remarks>
/// The app decides as one SAP user, the service approver, exactly as for credit memos. Which person in
/// the app may click is narrowed by SAP stage — <c>CreditNoteApprovals:RoleStageScopes</c>, which names
/// the wash bay's stage 'Factory Washbay'. In production that one stage holds both the wash bay's credit
/// memos (template 89) and the transfers into it (template 91 "Washbay Transfers").
/// </remarks>
internal static class SapTransferDecisionRules
{
    /// <summary>A request held on a transfer draft or a transfer request draft — the two this page holds.</summary>
    public static bool IsTransferRequest(SAPApprovalRequest request) =>
        string.Equals(request.ObjectType, SapObjectTypes.StockTransfer, StringComparison.Ordinal)
        || string.Equals(request.ObjectType, SapObjectTypes.InventoryTransferRequest, StringComparison.Ordinal);

    public static bool IsPending(SAPApprovalRequest request) =>
        string.Equals(request.Status, SapApprovalRequestStatuses.Pending, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the caller may decide <paramref name="request"/> now, and the sentence that says why not.
    /// A request that is not pending is simply not decidable, with no sentence: its status says it.
    /// </summary>
    public static (bool CanDecide, string? Note) Evaluate(
        SAPApprovalRequest request,
        SAPApprovalStage? stage,
        SAPUser? serviceApprover,
        string serviceApproverUserCode,
        CreditNoteApprovalStageFilter scope)
    {
        if (!IsPending(request))
        {
            return (false, null);
        }

        if (!scope.Admits(request.CurrentStage))
        {
            var where = string.IsNullOrWhiteSpace(stage?.Name) ? $"stage {request.CurrentStage}" : $"stage '{stage!.Name}'";
            return (false, $"This request waits at SAP {where}, which is not one of yours to decide.");
        }

        var (canDecide, _, note) = CreditNoteApprovalProjection.Flags(
            request,
            draft: null,
            stage,
            serviceApprover,
            serviceApproverUserCode,
            CreditNoteApprovalProjection.ServiceApproverAlreadyDecided(request, serviceApprover));

        return (canDecide, canDecide ? null : note);
    }
}
