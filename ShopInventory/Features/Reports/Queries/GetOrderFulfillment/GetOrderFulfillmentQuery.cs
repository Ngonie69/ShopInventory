using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetOrderFulfillment;

public sealed record GetOrderFulfillmentQuery(
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<OrderFulfillmentReportDto>>;
