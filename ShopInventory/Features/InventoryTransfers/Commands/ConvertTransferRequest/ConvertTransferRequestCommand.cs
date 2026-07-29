using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;

public sealed record ConvertTransferRequestCommand(
    int DocEntry,
    Guid UserId,
    Guid? StageId = null,
    string? Remarks = null,
    bool GenerateDocument = true) : IRequest<ErrorOr<TransferRequestConvertedResponseDto>>;
