using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CustomerDocuments.Commands.DispatchCustomerDocumentDeliveries;

/// <summary>
/// One pass of the delivery job: settle what was left uncertain, then prepare and send what is due.
/// </summary>
/// <param name="SapHeldBack">
/// SAP is in a declared outage or switched off. Documents that need SAP to render wait; the pass still
/// settles uncertain sends, which needs only the gateway.
/// </param>
public sealed record DispatchCustomerDocumentDeliveriesCommand(bool SapHeldBack = false)
    : IRequest<ErrorOr<DispatchCustomerDocumentDeliveriesResult>>;
