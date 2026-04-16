using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetLowStockAlerts;

public sealed record GetLowStockAlertsQuery(
    string? WarehouseCode = null,
    decimal? Threshold = null
) : IRequest<ErrorOr<LowStockAlertReportDto>>;
