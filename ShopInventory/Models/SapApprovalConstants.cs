namespace ShopInventory.Models;

/// <summary>
/// The literal values the Service Layer uses on the approval-procedure entities. Enums travel as their
/// member names (<c>arsPending</c>), and the display words are those names with the prefix stripped —
/// deliberately the same words <see cref="Entities.ApprovalRequestStatuses"/> uses for the local engine.
/// </summary>
public static class SapApprovalRequestStatuses
{
    public const string Pending = "arsPending";
    public const string Approved = "arsApproved";
    public const string NotApproved = "arsNotApproved";
    public const string Generated = "arsGenerated";
    public const string GeneratedByAuthorizer = "arsGeneratedByAuthorizer";
    public const string Cancelled = "arsCancelled";

    public static bool IsGenerated(string? status) =>
        string.Equals(status, Generated, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, GeneratedByAuthorizer, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>arsPending</c> → <c>Pending</c>; an unknown value is returned as it came.</summary>
    public static string ToDisplay(string? status) => SapEnumNames.StripPrefix(status, "ars");
}

/// <summary>The decision an approver records on a request line, and what SAP asks for in a PATCH.</summary>
public static class SapApprovalDecisions
{
    public const string Pending = "ardPending";
    public const string Approved = "ardApproved";
    public const string NotApproved = "ardNotApproved";

    public static string ToDisplay(string? decision) => SapEnumNames.StripPrefix(decision, "ard");
}

/// <summary>A document's own approval state (<c>AuthorizationStatus</c>).</summary>
public static class SapDocumentAuthorizationStatuses
{
    public const string Without = "dasWithout";
    public const string Pending = "dasPending";
    public const string Approved = "dasApproved";
    public const string Rejected = "dasRejected";
    public const string Generated = "dasGenerated";
    public const string GeneratedByAuthorizer = "dasGeneratedbyAuthorizer";
    public const string Cancelled = "dasCancelled";
}

public static class SapObjectTypes
{
    /// <summary>A/R credit memo (ORIN), as <c>ApprovalRequests.ObjectType</c> carries it.</summary>
    public const string CreditNote = "14";
}

public static class SapDocObjectCodes
{
    /// <summary>What a draft of an A/R credit memo reports as its <c>DocObjectCode</c>.</summary>
    public const string CreditNotes = "oCreditNotes";
}

public static class SapDocumentStatuses
{
    public const string Open = "bost_Open";
    public const string Closed = "bost_Close";
}

public static class SapYesNo
{
    public const string Yes = "tYES";
    public const string No = "tNO";
}

internal static class SapEnumNames
{
    public static string StripPrefix(string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length
            ? value[prefix.Length..]
            : value;
    }
}
