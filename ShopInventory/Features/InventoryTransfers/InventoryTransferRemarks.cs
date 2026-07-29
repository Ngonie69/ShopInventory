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
    {
        var trail = new List<string>
        {
            $"Requested by {pending.CreatedByName} on {FormatTime(pending.CreatedAtUtc)}"
        };

        var approvals = stages
            .SelectMany(stage => stage.Decisions)
            .Where(decision => decision.Decision == ApprovalDecisionValues.Approved)
            .OrderBy(decision => decision.DecidedAtUtc)
            .Select(decision => $"{decision.AuthorizerName} on {FormatTime(decision.DecidedAtUtc)}")
            .ToList();
        if (approvals.Count > 0)
            trail.Add($"Approved by {string.Join(", ", approvals)}");

        var trailText = Truncate(string.Join(Separator, trail), MaxLength);

        var text = (reason ?? pending.Comments)?.Trim();
        if (string.IsNullOrEmpty(text))
            return trailText;

        var budget = MaxLength - trailText.Length - Separator.Length - ReasonLabel.Length;
        return budget <= 0
            ? trailText
            : $"{ReasonLabel}{Truncate(text, budget)}{Separator}{trailText}";
    }

    private static string FormatTime(DateTime utc)
        => AuditService.ToCAT(utc).ToString("dd MMM yyyy HH:mm");

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
