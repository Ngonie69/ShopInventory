using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoicesByDateRange;

public sealed record GetInvoicesByDateRangeQuery(
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<List<InvoiceDto>>>;
