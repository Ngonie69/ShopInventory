using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoice;

public sealed record GetInvoiceQuery(
    int DocEntry
) : IRequest<ErrorOr<InvoiceDto>>;
