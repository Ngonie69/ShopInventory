using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetRoutes;

/// <summary>
/// The selling routes. <c>IncludeInactive</c> brings back retired ones too, which a report filter
/// needs because they still head historical days.
/// </summary>
public sealed record GetRoutesQuery(bool IncludeInactive = false)
    : IRequest<ErrorOr<List<RouteDto>>>;

public sealed record RouteDto(
    int Id,
    string Code,
    string Name,
    string? Territory,
    string? TruckRegNo,
    bool IsActive,
    int AssignedUserCount
);
