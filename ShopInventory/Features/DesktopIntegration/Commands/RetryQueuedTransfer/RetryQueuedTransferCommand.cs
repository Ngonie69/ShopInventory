using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.RetryQueuedTransfer;

public sealed record RetryQueuedTransferCommand(
    string ExternalReference
) : IRequest<ErrorOr<Success>>;
