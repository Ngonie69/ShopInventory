using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Commands.DeleteRoute;

/// <summary>
/// Takes a route off the routes page for good.
/// </summary>
/// <remarks>
/// Not the same thing as retiring it. A retired route still lists, still heads its historical days on
/// the page, and can be switched back on; a deleted one is gone from every list and refuses every
/// write. The row is kept all the same — see <c>RouteEntity.DeletedAt</c> — because trading days
/// already point at it, and because the seeder would put a seeded route back on the next start if the
/// row it matches on were missing. Refused while vans are still on the route, for the reason retiring
/// is: they would start the next morning with no territory and no truck.
/// </remarks>
public sealed record DeleteRouteCommand(int Id, Guid? ActingUserId) : IRequest<ErrorOr<Deleted>>;
