using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RouteCustomers.Commands.ImportRouteSchedule;

/// <summary>
/// Checks (<c>ValidateOnly</c>), or saves, the office's per-route calling schedule: which weekday each
/// van calls on each of its shops.
/// </summary>
/// <remarks>
/// This is the plan the CCR is measured against. <c>VanDayPlans</c> takes a truck-day's round from
/// these weekdays, and without them falls back to guessing the round from earlier visits, or to the
/// truck's whole customer list — which reads a truck that called on every shop due as 20%.
/// </remarks>
/// <param name="Request">The outlets read off the workbook, sheet by sheet, and whether only to check them.</param>
/// <param name="UserId">The signed-in operator, for the log.</param>
public sealed record ImportRouteScheduleCommand(
    RouteScheduleImportRequest Request,
    Guid UserId) : IRequest<ErrorOr<RouteScheduleImportResultDto>>;
