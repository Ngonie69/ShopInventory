using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.PostPendingTransferLinesInStock;

/// <summary>
/// Posts the part of an approved transfer the depot can fill, and leaves the short lines out.
/// </summary>
/// <remarks>
/// SAP refuses a whole transfer for one short line, so a van asking for 25 lines with 5 out of stock
/// otherwise gets none of the 20. This is the way to send it the 20 without re-keying the request.
/// </remarks>
public sealed record PostPendingTransferLinesInStockCommand(
    Guid PendingTransferId,
    Guid UserId) : IRequest<ErrorOr<PendingInventoryTransferDecisionResponseDto>>;
