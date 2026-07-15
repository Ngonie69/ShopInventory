using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.CloseTransferRequest;

public sealed class CloseTransferRequestHandler(IMediator mediator)
    : IRequestHandler<CloseTransferRequestCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        CloseTransferRequestCommand command,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new InventoryTransfers.Commands.CloseTransferRequest.CloseTransferRequestCommand(
                command.DocEntry,
                command.UserId),
            cancellationToken);

        return result.IsError ? result.Errors : Result.Deleted;
    }
}
