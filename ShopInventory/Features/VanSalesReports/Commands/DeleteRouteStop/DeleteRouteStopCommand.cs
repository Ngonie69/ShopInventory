using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesReports.Commands.DeleteRouteStop;

/// <summary>
/// Drops an area from a route's plan.
/// </summary>
/// <remarks>
/// A deactivation, not a delete, exactly as <c>DeleteRouteCustomerHandler</c> is: a round that has
/// stopped calling on an area and a round that never called on it are different histories, and only
/// one of them is a change the office would want to see. Keeping the row also keeps the seeder from
/// putting the stop back on the next start, which a delete would not.
/// </remarks>
public sealed record DeleteRouteStopCommand(int Id, Guid? ActingUserId) : IRequest<ErrorOr<Deleted>>;
