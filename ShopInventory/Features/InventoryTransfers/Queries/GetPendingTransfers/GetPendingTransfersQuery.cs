using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransfers;

/// <summary>
/// Lists direct inventory transfers held for approval. <c>Status</c> defaults to AwaitingApproval
/// ("all" lifts the filter) and <c>MineOnly</c> narrows the list to the caller's own submissions.
/// </summary>
public sealed record GetPendingTransfersQuery(
    Guid UserId,
    string? Status = null,
    string? WarehouseCode = null,
    bool MineOnly = false,
    int Page = 1,
    int PageSize = 20) : IRequest<ErrorOr<PendingInventoryTransferListResponseDto>>;
