using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CustomerDocuments.Commands.ScanNewInvoicesForDelivery;

/// <summary>
/// One pass of the invoice scan: read the invoices SAP has posted since the last look and queue an
/// automatic send for each one whose customer asked for them.
/// </summary>
/// <param name="SapHeldBack">SAP is in a declared outage; the pass makes no SAP call and moves nothing.</param>
public sealed record ScanNewInvoicesForDeliveryCommand(bool SapHeldBack = false)
    : IRequest<ErrorOr<ScanNewInvoicesForDeliveryResult>>;
