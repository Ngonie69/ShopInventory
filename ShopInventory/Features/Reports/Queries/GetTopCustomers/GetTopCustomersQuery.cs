using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetTopCustomers;

public sealed record GetTopCustomersQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    int TopCount = 10
) : IRequest<ErrorOr<TopCustomersReportDto>>;
