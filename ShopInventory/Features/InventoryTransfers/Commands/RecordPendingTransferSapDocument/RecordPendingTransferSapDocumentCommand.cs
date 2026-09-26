using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.RecordPendingTransferSapDocument;

/// <summary>
/// Records a SAP document somebody found in SAP as the post of a held transfer.
/// </summary>
/// <remarks>
/// For the post that timed out: SAP created the transfer and the answer never came back, so the
/// record reads PostFailed while the stock has already moved. Retrying would move it again; this
/// closes the record against the document that exists instead.
/// </remarks>
public sealed record RecordPendingTransferSapDocumentCommand(
    Guid PendingTransferId,
    Guid UserId,
    int SapDocNum) : IRequest<ErrorOr<PendingInventoryTransferDecisionResponseDto>>;
