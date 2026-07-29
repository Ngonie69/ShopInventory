using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.CancelPendingTransfer;

/// <summary>
/// Withdraws a transfer the caller submitted, before any decision has been recorded.
/// </summary>
public sealed record CancelPendingTransferCommand(
    Guid PendingTransferId,
    Guid UserId) : IRequest<ErrorOr<PendingInventoryTransferDecisionResponseDto>>;
