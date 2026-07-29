using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConvertTransferRequest;

public sealed class ConvertTransferRequestHandler(IMediator mediator)
    : IRequestHandler<ConvertTransferRequestCommand, ErrorOr<InventoryTransferCreatedResponseDto>>
{
    public async Task<ErrorOr<InventoryTransferCreatedResponseDto>> Handle(
        ConvertTransferRequestCommand command,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new InventoryTransfers.Commands.ConvertTransferRequest.ConvertTransferRequestCommand(
                command.DocEntry,
                command.UserId),
            cancellationToken);

        if (result.IsError)
            return result.Errors;

        return new InventoryTransferCreatedResponseDto
        {
            Message = result.Value.Message,
            Transfer = result.Value.Transfer
        };
    }
}
