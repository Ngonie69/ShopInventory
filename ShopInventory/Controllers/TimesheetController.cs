using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Features.Timesheets.Commands.CheckIn;
using ShopInventory.Features.Timesheets.Commands.CheckOut;
using ShopInventory.Features.Timesheets.Queries.GetActiveCheckIn;
using ShopInventory.Features.Timesheets.Queries.GetAssignedCustomers;
using ShopInventory.Features.Timesheets.Queries.GetTimesheets;
using ShopInventory.Features.Timesheets.Queries.GetTimesheetReport;
using ShopInventory.Models;
using System.Security.Claims;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class TimesheetController(IMediator mediator) : ApiControllerBase
{
    [HttpPost("check-in")]
    [RequirePermission(Permission.ManageTimesheets)]
    [ProducesResponseType(typeof(CheckInResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CheckIn(
        [FromBody] CheckInRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var username = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";

        var command = new CheckInCommand(
            userId.Value,
            username,
            request.CustomerCode,
            request.CustomerName,
            request.Latitude,
            request.Longitude,
            request.Notes);

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            value => CreatedAtAction(nameof(GetActive), value),
            errors => Problem(errors));
    }

    [HttpPost("check-out")]
    [RequirePermission(Permission.ManageTimesheets)]
    [ProducesResponseType(typeof(CheckOutResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CheckOut(
        [FromBody] CheckOutRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var username = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";

        var command = new CheckOutCommand(
            userId.Value,
            username,
            request.Latitude,
            request.Longitude,
            request.Notes);

        var result = await mediator.Send(command, cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    [HttpGet("active")]
    [RequirePermission(Permission.ManageTimesheets)]
    [ProducesResponseType(typeof(ActiveCheckInResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActive(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new GetActiveCheckInQuery(userId.Value), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    [HttpGet("assigned-customers")]
    [RequirePermission(Permission.ManageTimesheets)]
    [ProducesResponseType(typeof(List<AssignedCustomerDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAssignedCustomers(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new GetAssignedCustomersQuery(userId.Value), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    [HttpGet]
    [RequirePermission(Permission.ViewTimesheets)]
    [ProducesResponseType(typeof(TimesheetListResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTimesheets(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? username = null,
        [FromQuery] string? customerCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        // Merchandiser can only see their own timesheets
        if (User.IsInRole("Merchandiser") && !User.IsInRole("Admin"))
        {
            var currentUserId = GetCurrentUserId();
            userId = currentUserId;
        }

        // Normalize dates to UTC for PostgreSQL timestamptz compatibility
        var utcFromDate = fromDate.HasValue ? DateTime.SpecifyKind(fromDate.Value, DateTimeKind.Utc) : fromDate;
        var utcToDate = toDate.HasValue ? DateTime.SpecifyKind(toDate.Value, DateTimeKind.Utc) : toDate;

        var result = await mediator.Send(
            new GetTimesheetsQuery(page, pageSize, userId, username, customerCode, utcFromDate, utcToDate),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    [HttpGet("report")]
    [RequirePermission(Permission.ViewTimesheets)]
    [ProducesResponseType(typeof(TimesheetReportResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReport(
        [FromQuery] Guid? userId = null,
        [FromQuery] string? username = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        // Normalize dates to UTC for PostgreSQL timestamptz compatibility
        var from = fromDate.HasValue ? DateTime.SpecifyKind(fromDate.Value, DateTimeKind.Utc) : DateTime.UtcNow.AddDays(-30);
        var to = toDate.HasValue ? DateTime.SpecifyKind(toDate.Value, DateTimeKind.Utc) : DateTime.UtcNow;

        // Merchandiser can only see their own report
        if (User.IsInRole("Merchandiser") && !User.IsInRole("Admin"))
        {
            var currentUserId = GetCurrentUserId();
            userId = currentUserId;
        }

        var result = await mediator.Send(
            new GetTimesheetReportQuery(userId, username, from, to),
            cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}

public record CheckInRequest(
    string CustomerCode,
    string CustomerName,
    double? Latitude,
    double? Longitude,
    string? Notes);

public record CheckOutRequest(
    double? Latitude,
    double? Longitude,
    string? Notes);
