using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetCreditNoteSummary;

public sealed record GetCreditNoteSummaryQuery(
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<CreditNoteSummaryReportDto>>;
