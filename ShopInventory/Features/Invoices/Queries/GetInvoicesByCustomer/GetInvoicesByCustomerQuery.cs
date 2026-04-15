using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetInvoicesByCustomer;

public sealed record GetInvoicesByCustomerQuery(
    string CardCode,
    DateTime? FromDate,
    DateTime? ToDate,
    int? Page,
    int? PageSize
) : IRequest<ErrorOr<InvoiceDateResponseDto>>;
