using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.RetryPendingRequestEditApply;

/// <summary>
/// Re-attempts the SAP write for a transfer request change that was fully approved but failed
/// to apply.
/// </summary>
public sealed record RetryPendingRequestEditApplyCommand(
    Guid PendingEditId,
    Guid UserId) : IRequest<ErrorOr<PendingTransferRequestEditDecisionResponseDto>>;
