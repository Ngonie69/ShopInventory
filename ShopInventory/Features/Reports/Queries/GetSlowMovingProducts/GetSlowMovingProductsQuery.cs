using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetSlowMovingProducts;

public sealed record GetSlowMovingProductsQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    int DaysThreshold = 30
) : IRequest<ErrorOr<SlowMovingProductsReportDto>>;
