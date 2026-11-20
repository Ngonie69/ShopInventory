using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>
/// Builds the remarks a posted inventory transfer carries in SAP.
/// </summary>
/// <remarks>
/// SAP has no fields for who raised a transfer or who signed it off, so the whole audit trail has
/// to live in the one Comments column — that is the only place someone looking at the document in
/// SAP can read why the stock moved and on whose authority. Times are CAT, matching every other
/// timestamp shown to users.
/// </remarks>
public static class InventoryTransferRemarks
{
    /// <summary>OWTR.Comments is a single 254-character column in SAP Business One.</summary>
    public const int MaxLength = 254;

    private const string Separator = " | ";
    private const string ReasonLabel = "Reason: ";

    /// <summary>
    /// Composes the reason, the person who raised the transfer and everyone who approved it,
    /// each with its time. The audit trail is laid out first and the free-text reason takes
    /// whatever of the 254 characters is left, so a long reason can never push out the record
    /// of who authorised the movement.
    /// </summary>
    public static string Build(
        PendingInventoryTransferEntity pending,
        IReadOnlyList<ApprovalStageProgressDto> stages,
        string? reason = null)
        => Compose(
            pending,
            stages
                .SelectMany(stage => stage.Decisions)
                .Where(decision => decision.Decision == ApprovalDecisionValues.Approved)
                .OrderBy(decision => decision.DecidedAtUtc)
                .Select(decision => $"{decision.AuthorizerName} on {FormatTime(decision.DecidedAtUtc)}")
                .ToList(),
            reason);

    /// <summary>
    /// The same remarks for a factory transfer, which SAP's own approval signed off: each SAP approver
    /// with the time SAP recorded the decision.
    /// </summary>
    /// <param name="pending">The transfer; its creator is the requester.</param>
    /// <param name="reason">The requester's comments; the transfer's own when null.</param>
    /// <param name="sapApprovals">
    /// Who approved in SAP and when, in SAP's own clock — the company's local time (CAT), which is
    /// what <c>ApprovalRequestLines.UpdateDate/UpdateTime</c> hold, so it is written as it is.
    /// A null time names the approver alone.
    /// </param>
    public static string BuildForSapApproval(
        PendingInventoryTransferEntity pending,
        IReadOnlyList<(string Name, DateTime? DecidedAtCat)> sapApprovals,
        string? reason = null)
        => Compose(
            pending,
            sapApprovals
                .OrderBy(approval => approval.DecidedAtCat ?? DateTime.MaxValue)
                .Select(approval => approval.DecidedAtCat is DateTime at
                    ? $"{approval.Name} on {at.ToString(TimeFormat)}"
                    : approval.Name)
                .ToList(),
            reason);

    private static string Compose(PendingInventoryTransferEntity pending, List<string> approvals, string? reason)
    {
        var trail = new List<string>
        {
            $"Requested by {pending.CreatedByName} on {FormatTime(pending.CreatedAtUtc)}"
        };

        if (approvals.Count > 0)
            trail.Add($"Approved by {string.Join(", ", approvals)}");

        var trailText = Truncate(string.Join(Separator, trail), MaxLength);

        // The Web composes the requester's comments as "Reason: X | notes", so stored comments
        // usually carry the label already; drop it so SAP reads "Reason: " once, not twice.
        var text = (reason ?? pending.Comments)?.Trim();
        if (text is not null && text.StartsWith(ReasonLabel, StringComparison.OrdinalIgnoreCase))
            text = text[ReasonLabel.Length..].Trim();
        if (string.IsNullOrEmpty(text))
            return trailText;

        var budget = MaxLength - trailText.Length - Separator.Length - ReasonLabel.Length;
        return budget <= 0
            ? trailText
            : $"{ReasonLabel}{Truncate(text, budget)}{Separator}{trailText}";
    }

    private const string TimeFormat = "dd MMM yyyy HH:mm";

    private static string FormatTime(DateTime utc)
        => AuditService.ToCAT(utc).ToString(TimeFormat);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
