using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetProfitOverview;

public sealed record GetProfitOverviewQuery(
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<ProfitOverviewReportDto>>;
