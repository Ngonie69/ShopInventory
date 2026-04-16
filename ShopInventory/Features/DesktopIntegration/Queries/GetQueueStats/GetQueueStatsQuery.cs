using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetQueueStats;

public sealed record GetQueueStatsQuery() : IRequest<ErrorOr<InvoiceQueueStatsDto>>;
