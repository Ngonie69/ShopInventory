using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetInvoiceByDocNum;

public sealed record GetInvoiceByDocNumQuery(int DocNum) : IRequest<ErrorOr<InvoiceDto>>;
