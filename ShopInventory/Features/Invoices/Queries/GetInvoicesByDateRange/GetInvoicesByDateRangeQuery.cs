using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetInvoicesByDateRange;

public sealed record GetInvoicesByDateRangeQuery(
    DateTime FromDate,
    DateTime ToDate,
    int Page = 1,
    int PageSize = 20
) : IRequest<ErrorOr<InvoiceDateResponseDto>>;
