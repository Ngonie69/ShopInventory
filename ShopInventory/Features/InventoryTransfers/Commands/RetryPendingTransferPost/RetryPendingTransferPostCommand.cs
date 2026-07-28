using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.RetryPendingTransferPost;

/// <summary>
/// Re-attempts the SAP post for a transfer that was fully approved but failed to post.
/// </summary>
public sealed record RetryPendingTransferPostCommand(
    Guid PendingTransferId,
    Guid UserId) : IRequest<ErrorOr<PendingInventoryTransferDecisionResponseDto>>;
