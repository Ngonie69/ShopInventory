using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.DecidePendingTransfer;

/// <summary>
/// Records an approval decision (Approved or NotApproved) against a direct inventory transfer
/// that is being held pending approval. The final approval posts the transfer to SAP.
/// </summary>
public sealed record DecidePendingTransferCommand(
    Guid PendingTransferId,
    Guid UserId,
    string Decision,
    Guid? StageId = null,
    string? Remarks = null) : IRequest<ErrorOr<PendingInventoryTransferDecisionResponseDto>>;
