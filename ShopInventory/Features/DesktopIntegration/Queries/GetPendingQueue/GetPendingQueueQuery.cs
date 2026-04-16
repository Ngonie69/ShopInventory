using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPendingQueue;

public sealed record GetPendingQueueQuery(
    string? SourceSystem = null,
    int Limit = 100
) : IRequest<ErrorOr<List<InvoiceQueueStatusDto>>>;
