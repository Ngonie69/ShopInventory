using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPagedInvoices;

public sealed record GetPagedInvoicesQuery(
    int Page = 1,
    int PageSize = 20
) : IRequest<ErrorOr<List<InvoiceDto>>>;
