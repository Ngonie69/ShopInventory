using ErrorOr;
using MediatR;

namespace ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;

public sealed record CloseTransferRequestCommand(int DocEntry) : IRequest<ErrorOr<object>>;
