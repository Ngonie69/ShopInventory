using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Features.UserActivity.Queries.GetActivityDashboard;
using ShopInventory.Features.UserActivity.Queries.GetUserActivity;
using ShopInventory.Features.UserActivity.Queries.GetMyActivity;
using ShopInventory.Features.UserActivity.Queries.GetActivities;
using ShopInventory.Features.UserActivity.Queries.GetEntityActivities;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class UserActivityController(IMediator mediator) : ApiControllerBase
{
    [HttpGet("dashboard")]
    [RequirePermission(Permission.ViewAuditLogs)]
    public async Task<IActionResult> GetDashboard(
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetActivityDashboardQuery(startDate, endDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("user/{userId:guid}")]
    [RequirePermission(Permission.ViewAuditLogs)]
    public async Task<IActionResult> GetUserActivity(Guid userId, [FromQuery] int recentCount = 20, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetUserActivityQuery(userId, recentCount), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMyActivity([FromQuery] int recentCount = 20, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new GetMyActivityQuery(userId, recentCount), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet]
    [RequirePermission(Permission.ViewAuditLogs)]
    public async Task<IActionResult> GetActivities(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? action = null,
        [FromQuery] string? entityType = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetActivitiesQuery(page, pageSize, userId, action, entityType, startDate, endDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("entity/{entityType}/{entityId}")]
    [RequirePermission(Permission.ViewAuditLogs)]
    public async Task<IActionResult> GetEntityActivities(string entityType, string entityId, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetEntityActivitiesQuery(entityType, entityId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return null;

        return userId;
    }
}
