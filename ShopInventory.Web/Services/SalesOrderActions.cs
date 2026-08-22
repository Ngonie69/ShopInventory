using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Which actions a sales order will still accept, given its status. The sales-order list and the
/// mobile-orders list show the same orders through different filters, so an order's available
/// actions must not depend on which page it is being looked at from.
/// </summary>
/// <remarks>
/// <para>
/// These gates started as private copies on each page and drifted. The visible cost was approval:
/// a Web order is created as Draft and auto-posted in the same call, and a refused post returns it
/// to Draft with the reason recorded (<c>SalesOrderService</c> on the API side), so Draft is
/// exactly where an order needing a retry sits. The mobile page offered approval on Draft; the
/// sales-order page offered it on Pending alone, and stranded every one of those orders — the
/// cause could be edited away and no button was left that would retry the post.
/// </para>
/// <para>
/// ShopInventory.Web does not reference the API project, so this is a hand-mirrored copy of gates
/// that are enforced there: <c>SalesOrderService.ApproveAsync</c>, <c>DeleteAsync</c>, and
/// <c>IsValidStatusTransition</c>. Mirrored, not guessed — <c>SalesOrderActionsTests</c> pins each
/// set against the API's, the same way <c>SalesOrderSafetyTests</c> pins the API's own. A gate here
/// that is wider than the API's produces a button that fails; narrower, and an order strands.
/// </para>
/// <para>
/// Where the two pages already agreed, that answer was kept — merging these was a refactor, not a
/// redesign. Where they disagreed, the API decided, because the disagreement itself was the bug.
/// That rule leaves two actions deliberately narrower than the API allows: restoring a Pending
/// order to Draft, and cancelling a Rejected one. Both are legal transitions that neither page has
/// ever offered, and adding them would be new behaviour rather than a de-duplication.
/// </para>
/// </remarks>
public static class SalesOrderActions
{
    /// <summary>
    /// Whether an order can still be approved and posted to SAP.
    /// </summary>
    /// <remarks>
    /// Mirrors the API's own <c>CanApprove</c>. Draft belongs here as much as Pending: it is not a
    /// resting state but where a refused post leaves an order. Anything approvable must also be
    /// postable, or approval would move the order to a status the posting gate then refuses and it
    /// would strand a second time.
    /// </remarks>
    public static bool CanApprove(SalesOrderStatus status)
        => status is SalesOrderStatus.Draft or SalesOrderStatus.Pending;

    /// <summary>
    /// Whether an order's lines and header can still be changed.
    /// </summary>
    /// <remarks>
    /// The same set as <see cref="CanApprove(SalesOrderStatus)"/> today, but a separate rule with a
    /// separate enforcer — <c>SalesOrderService.UpdateAsync</c> — so it is kept separate here. This
    /// gate is what makes the approval one matter: editing is how a refused post gets fixed, and an
    /// order that can be edited but not re-approved has been fixed with nowhere to go.
    /// </remarks>
    public static bool CanEdit(SalesOrderStatus status)
        => status is SalesOrderStatus.Draft or SalesOrderStatus.Pending;

    /// <summary>
    /// Whether an order can be rejected — a decision recorded against an order still awaiting one.
    /// </summary>
    /// <remarks>
    /// The API reaches Rejected through <c>IsValidStatusTransition</c>, which allows it from Draft,
    /// Pending and OnHold and from nothing else. An Approved order is past the point of rejection;
    /// it gets cancelled instead.
    /// </remarks>
    public static bool CanReject(SalesOrderStatus status)
        => status is SalesOrderStatus.Draft or SalesOrderStatus.Pending or SalesOrderStatus.OnHold;

    /// <summary>
    /// Whether an order can be cancelled.
    /// </summary>
    /// <remarks>
    /// The API also allows Rejected to be cancelled. Neither list has ever offered that and both
    /// agreed on this set, so it is kept as-is: a rejected order is already finished with, and
    /// <see cref="CanRestoreToDraft"/> is the action it actually needs.
    /// </remarks>
    public static bool CanCancel(SalesOrderStatus status)
        => status is SalesOrderStatus.Draft
            or SalesOrderStatus.Pending
            or SalesOrderStatus.Approved
            or SalesOrderStatus.PartiallyFulfilled
            or SalesOrderStatus.OnHold;

    /// <summary>
    /// Whether an order can be put back to Draft to be worked on again.
    /// </summary>
    /// <remarks>
    /// The two pages disagreed on Rejected: the sales-order list offered it, the mobile list did
    /// not. The API permits Rejected to Draft, and a rejected order with no way back to Draft has
    /// no way forward at all, so the mobile list was the one that was wrong.
    /// <para>
    /// Pending to Draft is likewise legal on the API but is not offered here, because neither page
    /// has ever offered it and a Pending order already has Reject and Cancel available.
    /// </para>
    /// </remarks>
    public static bool CanRestoreToDraft(SalesOrderStatus status)
        => status is SalesOrderStatus.Cancelled or SalesOrderStatus.Rejected;

    /// <summary>
    /// Whether an order can be deleted outright.
    /// </summary>
    /// <remarks>
    /// This is the set <c>SalesOrderService.DeleteAsync</c> enforces. Each page was missing exactly
    /// one of it and the other page had that one: the sales-order list refused Pending, the mobile
    /// list refused Rejected. Neither omission was a rule — an order the API will delete showed a
    /// delete button or not depending on which list it was viewed from.
    /// <para>
    /// Both call sites keep their own Admin-only authorization around this. Who may delete is a
    /// separate question from what is deletable, and it is not this class's to answer.
    /// </para>
    /// </remarks>
    public static bool CanDelete(SalesOrderStatus status)
        => status is SalesOrderStatus.Draft
            or SalesOrderStatus.Pending
            or SalesOrderStatus.Cancelled
            or SalesOrderStatus.Rejected;

    /// <inheritdoc cref="CanApprove(SalesOrderStatus)"/>
    public static bool CanApprove(SalesOrderDto order)
        => IsOpen(order) && CanApprove(order.Status);

    /// <inheritdoc cref="CanEdit(SalesOrderStatus)"/>
    public static bool CanEdit(SalesOrderDto order)
        => IsOpen(order) && CanEdit(order.Status);

    /// <inheritdoc cref="CanReject(SalesOrderStatus)"/>
    public static bool CanReject(SalesOrderDto order)
        => IsOpen(order) && CanReject(order.Status);

    /// <inheritdoc cref="CanCancel(SalesOrderStatus)"/>
    public static bool CanCancel(SalesOrderDto order)
        => IsOpen(order) && CanCancel(order.Status);

    /// <inheritdoc cref="CanRestoreToDraft(SalesOrderStatus)"/>
    public static bool CanRestoreToDraft(SalesOrderDto order)
        => IsOpen(order) && CanRestoreToDraft(order.Status);

    /// <inheritdoc cref="CanDelete(SalesOrderStatus)"/>
    public static bool CanDelete(SalesOrderDto order)
        => IsOpen(order) && CanDelete(order.Status);

    /// <summary>
    /// An order that reached SAP is SAP's now. Every action above is local bookkeeping that would
    /// leave the two systems disagreeing, so all of them stop here — both pages already applied
    /// this to every gate, and keeping it in one place is what stops the next one being added
    /// without it.
    /// </summary>
    private static bool IsOpen(SalesOrderDto order) => !order.IsSynced;
}
