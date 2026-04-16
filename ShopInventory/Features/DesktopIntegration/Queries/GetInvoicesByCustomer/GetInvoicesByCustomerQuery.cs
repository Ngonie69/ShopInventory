using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoicesByCustomer;

public sealed record GetInvoicesByCustomerQuery(
    string CardCode,
    DateTime? FromDate = null,
    DateTime? ToDate = null
) : IRequest<ErrorOr<List<InvoiceDto>>>;
