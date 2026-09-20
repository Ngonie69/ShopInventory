using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Queries.GetRoutes;

/// <summary>
/// The selling routes. <c>IncludeInactive</c> brings back retired ones too, which a report filter
/// needs because they still head historical days.
/// </summary>
public sealed record GetRoutesQuery(bool IncludeInactive = false)
    : IRequest<ErrorOr<List<RouteDto>>>;

/// <remarks>
/// <c>TemperatureMinC</c> and <c>TemperatureMaxC</c> are the limits the load on this round is
/// held to, in Celsius, and are both null on a round that carries nothing chilled — which means
/// the route is never judged on temperature at all. <c>TemperatureProbeChannel</c> says which of
/// the tracker's four probes reads the load box, or null for the lowest-numbered probe that
/// reported: the fleet API publishes no capability flag for temperature, so which channel is the
/// box can only be told, never discovered.
/// </remarks>
public sealed record RouteDto(
    int Id,
    string Code,
    string Name,
    string? Territory,
    string? TruckRegNo,
    decimal? TemperatureMinC,
    decimal? TemperatureMaxC,
    byte? TemperatureProbeChannel,
    bool IsActive,
    int AssignedUserCount
);
