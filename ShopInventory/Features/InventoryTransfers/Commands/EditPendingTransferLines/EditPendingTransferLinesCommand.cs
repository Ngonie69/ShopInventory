using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.EditPendingTransferLines;

/// <summary>
/// Lowers or takes out lines of an approved transfer that has not reached SAP, so it can post.
/// </summary>
/// <remarks>
/// The usual case is a depot a few units short: SAP refuses the whole transfer for one line asking
/// 720 against 715 on hand, and before this the only ways out were to post without that line or to
/// withdraw the lot. Quantities may only go down and no item may be added, so the approval already
/// given still covers what posts; anything more is a new transfer and a new approval.
/// </remarks>
public sealed record EditPendingTransferLinesCommand(
    Guid PendingTransferId,
    Guid UserId,
    IReadOnlyList<EditPendingTransferLineDto> Lines,
    string? Reason) : IRequest<ErrorOr<PendingInventoryTransferDecisionResponseDto>>;
