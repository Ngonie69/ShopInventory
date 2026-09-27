using ErrorOr;
using MediatR;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransferStockCheck;

/// <summary>
/// Measures a held transfer's lines against the depot's stock as it stands now.
/// </summary>
/// <remarks>
/// What a person needs to see before choosing between retrying, posting the lines in stock and
/// withdrawing. The failure recorded on the transfer is only what the depot held at the last
/// attempt, which may be weeks old; this is a live read of one document's items, so it is cheap.
/// </remarks>
public sealed record GetPendingTransferStockCheckQuery(
    Guid PendingTransferId,
    Guid UserId) : IRequest<ErrorOr<PendingTransferStockCheckResult>>;
