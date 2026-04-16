using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.RetryQueuedInvoice;

public sealed record RetryQueuedInvoiceCommand(
    string ExternalReference
) : IRequest<ErrorOr<Success>>;
