using ShopInventory.Models;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>
/// The reference rows the approval pages name people, stages and templates by — read from SAP once
/// and held for a few minutes, because every row of the list asks for the same handful.
/// </summary>
/// <remarks>
/// A miss is a null, not an exception. A failure to reach SAP is an exception, so a caller that can
/// carry on without the name (the list) catches it, and one that cannot (the decision, which needs the
/// stage's approvers) lets it surface.
/// </remarks>
public interface ISapApprovalLookups
{
    /// <summary>The SAP user code the app records decisions as: <c>SAP:ApprovalApproverUsername</c>, else <c>SAP:Username</c>.</summary>
    string ServiceApproverUserCode { get; }

    Task<SAPUser?> GetServiceApproverAsync(CancellationToken cancellationToken);
    Task<SAPUser?> GetUserAsync(int internalKey, CancellationToken cancellationToken);
    Task<SAPApprovalTemplate?> GetTemplateAsync(int code, CancellationToken cancellationToken);
    Task<SAPApprovalStage?> GetStageAsync(int code, CancellationToken cancellationToken);
}
