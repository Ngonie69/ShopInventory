using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetPaymentSummary;

public sealed record GetPaymentSummaryQuery(
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<PaymentSummaryReportDto>>;
