using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;

/// <remarks>
/// <c>Username</c> is who is converting, for the audit trail of a SAP-approved draft the conversion
/// adds. Optional: the desktop path has none to give.
/// </remarks>
public sealed record ConvertTransferRequestCommand(
    int DocEntry,
    Guid UserId,
    Guid? StageId = null,
    string? Remarks = null,
    bool GenerateDocument = true,
    string? Username = null) : IRequest<ErrorOr<TransferRequestConvertedResponseDto>>;
