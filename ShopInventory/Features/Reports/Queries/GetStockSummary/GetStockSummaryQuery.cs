using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetStockSummary;

public sealed record GetStockSummaryQuery(
    string? WarehouseCode = null
) : IRequest<ErrorOr<StockSummaryReportDto>>;
