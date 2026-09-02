using ShopInventory.Models;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>The status words the list route accepts, and the SAP literals each one asks for.</summary>
public static class CreditNoteApprovalStatusFilters
{
    /// <summary>Awaiting a decision, or approved and not yet added — everything somebody can still act on. The default.</summary>
    public const string Open = "open";
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string All = "all";

    public static readonly IReadOnlyList<string> Known = [Open, Pending, Approved, All];

    public static bool IsKnown(string? filter) =>
        string.IsNullOrWhiteSpace(filter) || Known.Contains(filter.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string Normalise(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? Open : filter.Trim().ToLowerInvariant();

    public static IReadOnlyCollection<string> ToSapStatuses(string? filter) => Normalise(filter) switch
    {
        Pending => [SapApprovalRequestStatuses.Pending],
        Approved => [SapApprovalRequestStatuses.Approved],
        All =>
        [
            SapApprovalRequestStatuses.Pending,
            SapApprovalRequestStatuses.Approved,
            SapApprovalRequestStatuses.NotApproved,
            SapApprovalRequestStatuses.Generated,
            SapApprovalRequestStatuses.GeneratedByAuthorizer,
            SapApprovalRequestStatuses.Cancelled
        ],
        _ => [SapApprovalRequestStatuses.Pending, SapApprovalRequestStatuses.Approved]
    };
}
