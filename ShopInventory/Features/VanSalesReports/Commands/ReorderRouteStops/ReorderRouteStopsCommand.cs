using ErrorOr;
using MediatR;
using ShopInventory.Features.VanSalesReports.Queries.GetRouteStops;

namespace ShopInventory.Features.VanSalesReports.Commands.ReorderRouteStops;

/// <summary>
/// Puts one heading's stops into the order the van works them.
/// </summary>
/// <remarks>
/// A command of its own rather than a <c>Sequence</c> on each save, because reordering is one
/// decision about a list and not several about rows. Moving a stop up shifts every stop it passes, so
/// as separate saves it is two or more round trips that each leave the group briefly holding a
/// duplicate position — and a failure between them leaves it there for good.
/// <para>
/// The whole heading is named on every call. <see cref="StopIds"/> must be exactly the stops that
/// heading currently holds, so a page that has not seen a stop somebody else just added is refused
/// rather than quietly demoting it to last place.
/// </para>
/// </remarks>
public sealed record ReorderRouteStopsCommand(
    int RouteId,
    DayOfWeek? DayOfWeek,
    int? WeekNumber,
    int AlternateSet,
    IReadOnlyList<int> StopIds,
    Guid? ActingUserId
) : IRequest<ErrorOr<List<RouteStopDto>>>;
