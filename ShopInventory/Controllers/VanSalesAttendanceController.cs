using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Features.VanSalesAttendance.Queries.GetVanVisitReport;
using ShopInventory.Features.VanSalesAttendance.Queries.GetVanVisits;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Van sales check-in and check-out, for the portal.
///
/// Its own controller, its own route and its own permission. <c>TimesheetController</c> answers for
/// merchandisers and cannot be asked for a van; this one answers for vans and cannot be asked for a
/// merchandiser. Neither takes a channel from the caller, so there is no query string that crosses
/// between them.
///
/// Distinct from <c>VanSalesCompatibilityController</c>, which speaks the handset's legacy dialect on
/// <c>/api/vansales</c> and writes these rows. This is the read side, for the web app.
/// </summary>
[Route("api/van-sales/visits")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class VanSalesAttendanceController(IMediator mediator) : ApiControllerBase
{
    /// <summary>A page of van sales calls, newest first.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="userId">One rep, or every rep when omitted.</param>
    /// <param name="username">One rep by username.</param>
    /// <param name="customerCode">One shop.</param>
    /// <param name="fromDate">Inclusive lower bound on check-in time.</param>
    /// <param name="toDate">Inclusive upper bound on check-in time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [RequirePermission(Permission.ViewVanSalesAttendance)]
    [ProducesResponseType(typeof(VanVisitListResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVisits(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? username = null,
        [FromQuery] string? customerCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        // Normalize dates to UTC for PostgreSQL timestamptz compatibility.
        var utcFromDate = fromDate.HasValue ? DateTime.SpecifyKind(fromDate.Value, DateTimeKind.Utc) : fromDate;
        var utcToDate = toDate.HasValue ? DateTime.SpecifyKind(toDate.Value, DateTimeKind.Utc) : toDate;

        var result = await mediator.Send(
            new GetVanVisitsQuery(page, pageSize, userId, username, customerCode, utcFromDate, utcToDate),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    /// <summary>
    /// Time on the round, summarised per rep: calls, hours on site, and the daily and customer
    /// breakdowns behind them.
    /// </summary>
    /// <remarks>
    /// Dates here are CAT trading days, not instants, so they are taken as given rather than
    /// normalised to UTC the way <see cref="GetVisits"/> does it — the handler converts once, and
    /// counts days the same way the departure compliance report does.
    /// </remarks>
    /// <param name="fromDate">Inclusive CAT trading day. Defaults to 30 days back.</param>
    /// <param name="toDate">Inclusive CAT trading day. Defaults to today.</param>
    /// <param name="userId">One rep, or every rep when omitted.</param>
    /// <param name="username">One rep by username.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("report")]
    [RequirePermission(Permission.ViewVanSalesAttendance)]
    [ProducesResponseType(typeof(VanVisitReportResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReport(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? username = null,
        CancellationToken cancellationToken = default)
    {
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;

        var result = await mediator.Send(
            new GetVanVisitReportQuery(
                fromDate?.Date ?? today.AddDays(-30),
                toDate?.Date ?? today,
                userId,
                username),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }
}
