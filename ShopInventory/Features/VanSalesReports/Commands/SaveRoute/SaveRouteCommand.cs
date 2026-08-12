using ErrorOr;
using MediatR;
using ShopInventory.Features.VanSalesReports.Queries.GetRoutes;

namespace ShopInventory.Features.VanSalesReports.Commands.SaveRoute;

/// <summary>
/// Creates a route, or updates one. <c>Id</c> null means create.
/// </summary>
/// <remarks>
/// One command for both because the two differ in a single branch and the validation is identical.
/// The code is the stable identifier a report groups on, so it is required on create and may be
/// changed afterwards only while it remains unique — a route already named on stored trading days
/// keeps its snapshot of the old code either way, which is what makes a rename safe at all.
/// </remarks>
public sealed record SaveRouteCommand(
    int? Id,
    string Code,
    string Name,
    string? Territory,
    string? TruckRegNo,
    bool IsActive,
    Guid? ActingUserId
) : IRequest<ErrorOr<RouteDto>>;
