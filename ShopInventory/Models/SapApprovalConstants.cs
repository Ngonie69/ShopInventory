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

/// <summary>
/// An inventory transfer draft's own approval state. <c>StockTransfer</c> carries its own enum
/// (<c>StockTransferAuthorizationStatusEnum</c>), prefixed <c>sas</c> where documents use <c>das</c>.
/// </summary>
public static class SapStockTransferAuthorizationStatuses
{
    public const string Without = "sasWithout";
    public const string Pending = "sasPending";
    public const string Approved = "sasApproved";
    public const string Rejected = "sasRejected";
    public const string Generated = "sasGenerated";
    public const string GeneratedByAuthorizer = "sasGeneratedbyAuthorizer";
    public const string Cancelled = "sasCancelled";

    /// <summary><c>sasApproved</c> → <c>Approved</c>; an unknown value is returned as it came.</summary>
    public static string ToDisplay(string? status) => SapEnumNames.StripPrefix(status, "sas");
}

public static class SapObjectTypes
{
    /// <summary>A/R credit memo (ORIN), as <c>ApprovalRequests.ObjectType</c> carries it.</summary>
    public const string CreditNote = "14";

    /// <summary>Inventory transfer (OWTR), as <c>ApprovalRequests.ObjectType</c> carries it.</summary>
    public const string StockTransfer = "67";

    /// <summary>
    /// Inventory transfer request (OWTQ), as <c>ApprovalRequests.ObjectType</c> carries it. SAP's object
    /// number; not yet read back from a live request draft.
    /// </summary>
    public const string InventoryTransferRequest = "1250000001";
}

public static class SapDocObjectCodes
{
    /// <summary>What a draft of an A/R credit memo reports as its <c>DocObjectCode</c>.</summary>
    public const string CreditNotes = "oCreditNotes";

    /// <summary>
    /// What an inventory transfer draft reports as its <c>DocObjectCode</c> on <c>StockTransferDrafts</c>:
    /// the object number, not an enum name. Read back from KEFALOS_TEST_3 on 2026-11-06.
    /// </summary>
    public const string StockTransfer = "67";

    /// <summary>
    /// What an inventory transfer request draft reports as its <c>DocObjectCode</c> on the same set. Taken
    /// to be its object number, as the transfer's is; not yet read back from a live request draft.
    /// </summary>
    public const string InventoryTransferRequest = "1250000001";
}

/// <summary>What a stock transfer line names as its <c>BaseType</c>.</summary>
public static class SapStockTransferBaseTypes
{
    /// <summary>The line was copied from an inventory transfer request in the B1 client.</summary>
    public const string InventoryTransferRequest = "InventoryTransferRequest";

    /// <summary>The same base type as its object number, which some Service Layer builds send instead.</summary>
    public const string InventoryTransferRequestObjectNumber = "1250000001";

    public static bool IsTransferRequest(string? baseType) =>
        string.Equals(baseType, InventoryTransferRequest, StringComparison.OrdinalIgnoreCase)
        || string.Equals(baseType, InventoryTransferRequestObjectNumber, StringComparison.Ordinal);
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
