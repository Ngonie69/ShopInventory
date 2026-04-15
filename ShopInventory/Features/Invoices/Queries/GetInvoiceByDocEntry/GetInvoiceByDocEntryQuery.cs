using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetInvoiceByDocEntry;

public sealed record GetInvoiceByDocEntryQuery(int DocEntry) : IRequest<ErrorOr<InvoiceDto>>;
