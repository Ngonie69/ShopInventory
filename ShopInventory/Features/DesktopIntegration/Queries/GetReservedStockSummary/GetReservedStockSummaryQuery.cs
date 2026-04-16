using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetReservedStockSummary;

public sealed record GetReservedStockSummaryQuery(
    string WarehouseCode,
    string? ItemCodes
) : IRequest<ErrorOr<List<ReservedStockSummaryDto>>>;
