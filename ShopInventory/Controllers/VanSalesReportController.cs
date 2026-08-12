using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.Features.VanSalesReports.Commands.SaveRoute;
using ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;
using ShopInventory.Features.VanSalesReports.Queries.GetRoutes;
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
    [RequirePermission(Permission.ViewTimesheets)]
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

    /// <summary>The selling routes, for the report's filter and for assigning a van to one.</summary>
    /// <param name="includeInactive">Bring back retired routes too; they still head historical days.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("routes")]
    [RequirePermission(Permission.ViewTimesheets)]
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

/// <summary>A route as the portal submits it. There is no delete: a route names historical days.</summary>
public record SaveRouteRequest(
    string Code,
    string Name,
    string? Territory,
    string? TruckRegNo,
    bool IsActive = true);
