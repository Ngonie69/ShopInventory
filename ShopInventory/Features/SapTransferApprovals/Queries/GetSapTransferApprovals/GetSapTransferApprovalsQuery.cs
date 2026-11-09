using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;

/// <summary>One page of the inventory transfer drafts SAP has approved and nobody has added yet.</summary>
/// <param name="Page">Which page, when reading by offset. Advisory when a cursor is given.</param>
/// <param name="PageSize">How many rows the page holds.</param>
/// <param name="BeforeDocEntry">
/// The cursor from the previous page's <c>NextCursor</c>. Given, the page continues below that draft
/// DocEntry; null reads from the top and <paramref name="Page"/> offsets into the queue.
/// </param>
/// <param name="Warehouse">Only drafts leaving or entering this warehouse.</param>
/// <param name="CallerUserId">
/// The caller's account, whose role and warehouses decide which drafts they see — see
/// <see cref="ISapTransferApprovalAccess"/>. Null is a service caller and sees every warehouse.
/// </param>
/// <param name="Filter">
/// The page's other filters — source and destination warehouses, creation day, attachment, draft
/// number. Its <c>Warehouse</c> is ignored in favour of <paramref name="Warehouse"/>.
/// </param>
public sealed record GetSapTransferApprovalsQuery(
    int Page,
    int PageSize,
    int? BeforeDocEntry = null,
    string? Warehouse = null,
    Guid? CallerUserId = null,
    SapTransferDraftFilter? Filter = null)
    : IRequest<ErrorOr<SapTransferApprovalListResponseDto>>
{
    /// <summary>Everything SAP is asked to narrow by, in one value.</summary>
    public SapTransferDraftFilter EffectiveFilter =>
        (Filter ?? new SapTransferDraftFilter()) with
        {
            Warehouse = string.IsNullOrWhiteSpace(Warehouse) ? null : Warehouse.Trim()
        };
}
