namespace ShopInventory.Models;

/// <summary>
/// What a person narrows the credit note approval queue by, beyond its status and stage scope. Every
/// field is a column of SAP's <c>ApprovalRequests</c> set, so the filter goes to SAP and the count,
/// the tiles and the paging cursor all describe the narrowed queue.
/// </summary>
/// <param name="RaisedFrom">Only requests SAP created on or after this day.</param>
/// <param name="RaisedTo">Only requests SAP created on or before this day.</param>
/// <param name="OriginatorId">Only requests raised by this SAP user (<c>Users.InternalKey</c>).</param>
/// <remarks>
/// The customer is not here on purpose. It lives on the draft, not the request, and the only way to
/// filter by it in SAP is a <c>$crossjoin</c> with <c>Drafts</c> — which this Service Layer will list
/// but refuses to count (<c>-2013</c>), and matches case-sensitively. Measured 2026-11-09.
/// </remarks>
public sealed record CreditNoteApprovalRequestFilter(
    DateTime? RaisedFrom = null,
    DateTime? RaisedTo = null,
    int? OriginatorId = null)
{
    public static readonly CreditNoteApprovalRequestFilter None = new();

    public bool IsEmpty => RaisedFrom is null && RaisedTo is null && OriginatorId is null;
}
