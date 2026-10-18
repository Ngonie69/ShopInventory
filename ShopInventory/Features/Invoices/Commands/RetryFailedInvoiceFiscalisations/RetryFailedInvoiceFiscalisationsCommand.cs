using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Invoices.Commands.RetryFailedInvoiceFiscalisations;

/// <summary>One scheduled pass over the SAP invoices whose fiscalisation failed.</summary>
public sealed record RetryFailedInvoiceFiscalisationsCommand : IRequest<ErrorOr<RetryFailedInvoiceFiscalisationsResult>>;
