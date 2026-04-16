using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.CloseTransferRequest;

public sealed record CloseTransferRequestCommand(
    int DocEntry
) : IRequest<ErrorOr<Deleted>>;
