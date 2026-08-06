using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransfers;

/// <summary>
/// Lists direct inventory transfers held for approval. <c>Status</c> defaults to AwaitingApproval
/// ("all" lifts the filter) and <c>MineOnly</c> narrows the list to the caller's own submissions.
///
/// <c>FromDate</c> and <c>ToDate</c> bound when the transfer was raised, both inclusive. They exist
/// so a caller wanting only a count for a day — a dashboard, say — can ask for one page of one row
/// and read <c>TotalCount</c>, rather than paging the whole table and counting client-side.
/// </summary>
public sealed record GetPendingTransfersQuery(
    Guid UserId,
    string? Status = null,
    string? WarehouseCode = null,
    bool MineOnly = false,
    int Page = 1,
    int PageSize = 20,
    DateTime? FromDate = null,
    DateTime? ToDate = null) : IRequest<ErrorOr<PendingInventoryTransferListResponseDto>>;
