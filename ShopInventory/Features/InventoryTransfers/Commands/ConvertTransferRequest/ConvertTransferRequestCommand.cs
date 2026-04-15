using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;

public sealed record ConvertTransferRequestCommand(int DocEntry) : IRequest<ErrorOr<TransferRequestConvertedResponseDto>>;
