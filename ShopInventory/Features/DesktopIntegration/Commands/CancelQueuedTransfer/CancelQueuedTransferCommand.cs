using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.CancelQueuedTransfer;

public sealed record CancelQueuedTransferCommand(
    string ExternalReference,
    string? CancelledBy
) : IRequest<ErrorOr<Deleted>>;
