using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoiceByDocNum;

public sealed record GetInvoiceByDocNumQuery(
    int DocNum
) : IRequest<ErrorOr<InvoiceDto>>;
