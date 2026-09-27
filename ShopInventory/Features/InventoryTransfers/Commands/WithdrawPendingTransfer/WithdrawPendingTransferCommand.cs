using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.WithdrawPendingTransfer;

/// <summary>
/// Closes an approved transfer that failed to post, so it stops waiting for a post nobody intends.
/// </summary>
/// <remarks>
/// Separate from <c>CancelPendingTransferCommand</c>, which is the originator taking back a request
/// before anyone decided on it. This one overrules an approval, so it is for the people who can act
/// on the depot's stock, and it needs a reason.
/// </remarks>
public sealed record WithdrawPendingTransferCommand(
    Guid PendingTransferId,
    Guid UserId,
    string Reason) : IRequest<ErrorOr<PendingInventoryTransferDecisionResponseDto>>;
