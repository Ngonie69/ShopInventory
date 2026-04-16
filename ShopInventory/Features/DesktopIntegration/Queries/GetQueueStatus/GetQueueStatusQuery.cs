using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetQueueStatus;

public sealed record GetQueueStatusQuery(
    string ExternalReference
) : IRequest<ErrorOr<InvoiceQueueStatusDto>>;
