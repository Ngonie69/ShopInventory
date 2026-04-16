using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetStockMovement;

public sealed record GetStockMovementQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    string? WarehouseCode = null
) : IRequest<ErrorOr<StockMovementReportDto>>;
