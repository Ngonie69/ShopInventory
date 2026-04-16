using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.CancelQueuedInvoice;

public sealed record CancelQueuedInvoiceCommand(
    string ExternalReference,
    string? CancelledBy
) : IRequest<ErrorOr<Deleted>>;
