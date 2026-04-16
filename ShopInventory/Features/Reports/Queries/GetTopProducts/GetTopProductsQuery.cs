using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetTopProducts;

public sealed record GetTopProductsQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    int TopCount = 10,
    string? WarehouseCode = null
) : IRequest<ErrorOr<TopProductsReportDto>>;
