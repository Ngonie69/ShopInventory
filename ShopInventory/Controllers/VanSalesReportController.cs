using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.Features.VanSalesReports.Commands.DeleteRouteStop;
using ShopInventory.Features.VanSalesReports.Commands.ReorderRouteStops;
using ShopInventory.Features.VanSalesReports.Commands.SaveRoute;
using ShopInventory.Features.VanSalesReports.Commands.SaveRouteStop;
using ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;
using ShopInventory.Features.VanSalesReports.Queries.GetRouteStops;
using ShopInventory.Features.VanSalesReports.Queries.GetRoutes;
using ShopInventory.Features.VanSalesReports.Queries.GetVanReplenishmentReport;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesCoverageReport;
using ShopInventory.Features.VanSalesReports.Queries.GetVanStockReport;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesPerformanceReport;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Reporting over the vans, for the portal.
///
/// Separate from <c>VanSalesCompatibilityController</c> on purpose: that one exists to speak the
/// handset's legacy dialect, envelopes and snake_case and all, and is not free to change. This is a
/// plain API for the web app and should stay one.
/// </summary>
[Route("api/van-sales")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class VanSalesReportController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// The departure compliance report: a row per rep per trading day, with CCR, PCR, AOV, the
    /// takings by tender and the day's mileage.
    /// </summary>
    /// <param name="fromDate">Inclusive CAT trading day. Defaults to 30 days back.</param>
    /// <param name="toDate">Inclusive CAT trading day. Defaults to today.</param>
    /// <param name="userId">One rep, or every rep when omitted.</param>
    /// <param name="routeCode">
    /// One route. Days with no departure record are excluded when this is set, because nothing on a
    /// loose visit says which route it belonged to.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("compliance-report")]
    [RequirePermission(Permission.ViewVanSalesAttendance)]
    [ProducesResponseType(typeof(DepartureComplianceReportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetComplianceReport(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? routeCode = null,
        CancellationToken cancellationToken = default)
    {
        // Dates here are CAT trading days, not instants, so they are taken as given rather than
        // normalised to UTC the way the timesheet endpoints do it — the handler converts once, where
        // it knows which of the three tables needs which conversion.
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;

        var result = await mediator.Send(
            new GetDepartureComplianceReportQuery(
                fromDate?.Date ?? today.AddDays(-30),
                toDate?.Date ?? today,
                userId,
                routeCode),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    /// <summary>
    /// The van sales performance report: what sold over a period, cut by territory and route, by rep,
    /// by item, and over time, with the price actually achieved per item and the shape of the drops.
    /// </summary>
    /// <remarks>
    /// Reads the same fact stream the compliance report does, so the two agree by construction on a
    /// period's gross takings and its productive calls.
    /// </remarks>
    /// <param name="fromDate">Inclusive CAT trading day. Defaults to 30 days back.</param>
    /// <param name="toDate">Inclusive CAT trading day. Defaults to today.</param>
    /// <param name="userId">One rep, or every rep when omitted.</param>
    /// <param name="routeCode">
    /// One route. Sales whose rep opened no departure record are excluded when this is set, for the
    /// same reason the compliance report excludes them — nothing on such a sale says it belonged here.
    /// </param>
    /// <param name="topItems">How many items to rank. Zero or less returns all of them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("performance-report")]
    [RequirePermission(Permission.ViewVanSalesAttendance)]
    [ProducesResponseType(typeof(VanSalesPerformanceReportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPerformanceReport(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? routeCode = null,
        [FromQuery] int topItems = 50,
        CancellationToken cancellationToken = default)
    {
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;

        var result = await mediator.Send(
            new GetVanSalesPerformanceReportQuery(
                fromDate?.Date ?? today.AddDays(-30),
                toDate?.Date ?? today,
                userId,
                routeCode,
                topItems),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    /// <summary>
    /// The van sales coverage report: who the vans are reaching and who they are losing — the rate
    /// trends, the shops on the books that were not reached, outlet churn, the win-back register,
    /// route concentration and how the location record is holding up.
    /// </summary>
    /// <remarks>
    /// Reads further back than the period it reports on: the base's opening state needs a full lapse
    /// window behind it, and telling a genuinely new outlet from a returning one needs an unbounded
    /// look at when each shop first bought. Both are local reads.
    /// </remarks>
    /// <param name="fromDate">Inclusive CAT trading day. Defaults to 90 days back.</param>
    /// <param name="toDate">Inclusive CAT trading day. Defaults to today.</param>
    /// <param name="userId">One rep, or every rep when omitted.</param>
    /// <param name="routeCode">One route. Sales with no departure record are excluded when set.</param>
    /// <param name="lapseDays">
    /// How long a shop may go without buying before it counts as lapsed. Deliberately not the
    /// route-customer pages' dormancy threshold, which answers a narrower question about one shop.
    /// </param>
    /// <param name="granularity">How the churn and rate series are bucketed: Week or Month.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("coverage-report")]
    [RequirePermission(Permission.ViewVanSalesAttendance)]
    [ProducesResponseType(typeof(VanSalesCoverageReportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCoverageReport(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? routeCode = null,
        [FromQuery] int lapseDays = 90,
        [FromQuery] VanSalesCoverageGranularity granularity = VanSalesCoverageGranularity.Month,
        CancellationToken cancellationToken = default)
    {
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;

        var result = await mediator.Send(
            new GetVanSalesCoverageReportQuery(
                fromDate?.Date ?? today.AddDays(-90),
                toDate?.Date ?? today,
                userId,
                routeCode,
                lapseDays,
                granularity),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    /// <summary>
    /// The van replenishment report: how well the depots are keeping the vans stocked, and which
    /// restock requests are stuck.
    /// </summary>
    /// <remarks>
    /// Reads the pending-transfer table rather than the daily stock snapshot. Snapshots are a
    /// desktop-app feature that van sales never write to, and the job that fills them is off by
    /// default — a report built on them would silently report nothing.
    /// </remarks>
    /// <param name="fromDate">Inclusive CAT trading day. Defaults to 30 days back.</param>
    /// <param name="toDate">Inclusive CAT trading day. Defaults to today.</param>
    /// <param name="vanWarehouseCode">One van's warehouse, or every van when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("replenishment-report")]
    [RequirePermission(Permission.ViewVanSalesAttendance)]
    [ProducesResponseType(typeof(VanReplenishmentReportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetReplenishmentReport(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? vanWarehouseCode = null,
        CancellationToken cancellationToken = default)
    {
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;

        var result = await mediator.Send(
            new GetVanReplenishmentReportQuery(
                fromDate?.Date ?? today.AddDays(-30),
                toDate?.Date ?? today,
                vanWarehouseCode),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    /// <summary>
    /// The van stock report: what each van was loaded with, what sold off it, what the next morning
    /// found, which lines are riding the round without selling, and what is about to expire.
    /// </summary>
    /// <remarks>
    /// Built on the morning stock snapshot, whose running quantity no van sales path maintains — so
    /// the load comes from the snapshot and what sold comes from the sales themselves. Reconciliation
    /// is morning to morning and is only computed across consecutive snapshots; a missing day is
    /// reported as a break rather than bridged.
    /// </remarks>
    /// <param name="fromDate">Inclusive CAT trading day. Defaults to 14 days back.</param>
    /// <param name="toDate">Inclusive CAT trading day. Defaults to today.</param>
    /// <param name="vanWarehouseCode">One van's warehouse, or every van when omitted.</param>
    /// <param name="deadStockDays">Days carried without a sale before a line counts as dead.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("stock-report")]
    [RequirePermission(Permission.ViewVanSalesAttendance)]
    [ProducesResponseType(typeof(VanStockReportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetStockReport(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? vanWarehouseCode = null,
        [FromQuery] int deadStockDays = 14,
        CancellationToken cancellationToken = default)
    {
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;

        var result = await mediator.Send(
            new GetVanStockReportQuery(
                fromDate?.Date ?? today.AddDays(-14),
                toDate?.Date ?? today,
                vanWarehouseCode,
                deadStockDays),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    /// <summary>The selling routes, for the report's filter and for assigning a van to one.</summary>
    /// <remarks>
    /// Route names are reference data, not attendance, and this endpoint has two unrelated callers:
    /// the compliance report's filter, and the user editor, where assigning a rep to a route is part
    /// of editing the user. Gating it on van attendance alone would have emptied the editor's route
    /// picker for everyone who administers users without overseeing vans — and silently, because the
    /// portal service swallows the failure and returns an empty list. Any one of these is enough.
    /// </remarks>
    /// <param name="includeInactive">Bring back retired routes too; they still head historical days.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("routes")]
    [RequirePermission(
        Permission.ViewVanSalesAttendance,
        Permission.ViewUsers,
        Permission.CreateMerchandiserAccounts)]
    [ProducesResponseType(typeof(List<RouteDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRoutes(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetRoutesQuery(includeInactive), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    /// <summary>Creates a route.</summary>
    /// <param name="request">The route's code, name, territory and truck.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("routes")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(typeof(RouteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateRoute(
        [FromBody] SaveRouteRequest request,
        CancellationToken cancellationToken)
    {
        return await SaveAsync(null, request, cancellationToken);
    }

    /// <summary>Updates a route.</summary>
    /// <param name="id">The route to update.</param>
    /// <param name="request">The route's code, name, territory and truck.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPut("routes/{id:int}")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(typeof(RouteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateRoute(
        int id,
        [FromBody] SaveRouteRequest request,
        CancellationToken cancellationToken)
    {
        return await SaveAsync(id, request, cancellationToken);
    }

    /// <summary>
    /// The areas the routes work — the published schedule, a row per stop.
    /// </summary>
    /// <remarks>
    /// Gated the same way as <see cref="GetRoutes"/> and for the same reason: this is reference data
    /// with more than one unrelated caller, and gating it on van attendance alone would empty the
    /// schedule for everyone who administers routes without overseeing vans, silently.
    /// </remarks>
    /// <param name="routeId">One route, or every route when omitted.</param>
    /// <param name="includeInactive">Bring back stops dropped from the plan too.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("route-stops")]
    [RequirePermission(
        Permission.ViewVanSalesAttendance,
        Permission.ViewUsers,
        Permission.CreateMerchandiserAccounts)]
    [ProducesResponseType(typeof(List<RouteStopDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRouteStops(
        [FromQuery] int? routeId = null,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetRouteStopsQuery(routeId, includeInactive),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    /// <summary>Adds an area to a route's plan.</summary>
    /// <param name="request">The route, the area, and when it is worked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("route-stops")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(typeof(RouteStopDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateRouteStop(
        [FromBody] SaveRouteStopRequest request,
        CancellationToken cancellationToken)
    {
        return await SaveStopAsync(null, request, cancellationToken);
    }

    /// <summary>Edits an area on a route's plan.</summary>
    /// <param name="id">The stop to update.</param>
    /// <param name="request">The route, the area, and when it is worked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPut("route-stops/{id:int}")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(typeof(RouteStopDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateRouteStop(
        int id,
        [FromBody] SaveRouteStopRequest request,
        CancellationToken cancellationToken)
    {
        return await SaveStopAsync(id, request, cancellationToken);
    }

    /// <summary>Puts one weekday's — or one cycle week's — stops into the order the van works them.</summary>
    /// <param name="request">The heading, and its stops in their new order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("route-stops/reorder")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(typeof(List<RouteStopDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReorderRouteStops(
        [FromBody] ReorderRouteStopsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new ReorderRouteStopsCommand(
                request.RouteId,
                request.DayOfWeek,
                request.WeekNumber,
                request.AlternateSet,
                request.StopIds ?? [],
                UserClaimReader.GetUserId(User)),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    /// <summary>Drops an area from a route's plan. The row is kept and deactivated.</summary>
    /// <param name="id">The stop to drop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpDelete("route-stops/{id:int}")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRouteStop(
        int id,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new DeleteRouteStopCommand(id, UserClaimReader.GetUserId(User)),
            cancellationToken);

        return result.Match<IActionResult>(
            _ => NoContent(),
            errors => Problem(errors));
    }

    private async Task<IActionResult> SaveStopAsync(
        int? id,
        SaveRouteStopRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new SaveRouteStopCommand(
                id,
                request.RouteId,
                request.Name,
                request.DayOfWeek,
                request.WeekNumber,
                request.AlternateSet,
                request.Sequence,
                request.IsActive,
                UserClaimReader.GetUserId(User)),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    private async Task<IActionResult> SaveAsync(
        int? id,
        SaveRouteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new SaveRouteCommand(
                id,
                request.Code,
                request.Name,
                request.Territory,
                request.TruckRegNo,
                request.IsActive,
                UserClaimReader.GetUserId(User)),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }
}

/// <summary>
/// A planned stop as the portal submits it.
/// </summary>
/// <remarks>
/// <c>DayOfWeek</c> and <c>WeekNumber</c> are both optional and mean different things by their
/// absence: no weekday is an upcountry run that commits to a week rather than a morning, and no week
/// is a round that repeats every week. Null in both is a stop with no schedule yet, which is a
/// legitimate state and not an error.
/// </remarks>
public record SaveRouteStopRequest(
    int RouteId,
    string Name,
    DayOfWeek? DayOfWeek = null,
    int? WeekNumber = null,
    int AlternateSet = 0,
    int? Sequence = null,
    bool IsActive = true);

/// <summary>
/// One heading's stops in their new order, as the portal submits them.
/// </summary>
/// <remarks>
/// The heading is named the same way a stop names it — day, week and alternative set, with the same
/// meanings for null. <c>StopIds</c> must be exactly the stops that heading holds; a partial list is
/// refused rather than applied, because it comes from a page that has gone stale.
/// </remarks>
public record ReorderRouteStopsRequest(
    int RouteId,
    List<int> StopIds,
    DayOfWeek? DayOfWeek = null,
    int? WeekNumber = null,
    int AlternateSet = 0);

/// <summary>A route as the portal submits it. There is no delete: a route names historical days.</summary>
public record SaveRouteRequest(
    string Code,
    string Name,
    string? Territory,
    string? TruckRegNo,
    bool IsActive = true);
