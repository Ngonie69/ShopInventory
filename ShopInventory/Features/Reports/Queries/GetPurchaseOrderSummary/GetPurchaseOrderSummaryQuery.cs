using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetPurchaseOrderSummary;

public sealed record GetPurchaseOrderSummaryQuery(
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<PurchaseOrderSummaryReportDto>>;
