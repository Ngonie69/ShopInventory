using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetSalesSummary;

public sealed record GetSalesSummaryQuery(
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<SalesSummaryReportDto>>;
