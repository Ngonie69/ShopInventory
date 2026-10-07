using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.EditPendingTransferLines;

/// <summary>
/// Changes the lines of an approved transfer that has not reached SAP: lowers, raises or takes out a
/// line, or adds an item.
/// </summary>
/// <remarks>
/// The usual case is a depot a few units short: SAP refuses the whole transfer for one line asking
/// 720 against 715 on hand, and before this the only ways out were to post without that line or to
/// withdraw the lot. Raising a line or adding an item goes past what the approver signed off, and is
/// allowed on purpose: the transfer stays approved and the audit log records each change, its reason
/// and who made it, rather than the transfer going back round the approval stages.
/// </remarks>
public sealed record EditPendingTransferLinesCommand(
    Guid PendingTransferId,
    Guid UserId,
    IReadOnlyList<EditPendingTransferLineDto> Lines,
    string? Reason,
    IReadOnlyList<AddPendingTransferLineDto>? AddedLines = null) : IRequest<ErrorOr<PendingInventoryTransferDecisionResponseDto>>;
