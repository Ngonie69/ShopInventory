using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for user activity dashboard
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class UserActivityController : ControllerBase
{
    private readonly IUserActivityService _userActivityService;
    private readonly ILogger<UserActivityController> _logger;

    public UserActivityController(
        IUserActivityService userActivityService,
        ILogger<UserActivityController> logger)
    {
        _userActivityService = userActivityService;
        _logger = logger;
    }

    /// <summary>
    /// Get user activity dashboard
    /// </summary>
    /// <param name="startDate">Start date (default: today)</param>
    /// <param name="endDate">End date (default: now)</param>
    /// <returns>Dashboard data</returns>
    [HttpGet("dashboard")]
    [RequirePermission(Permission.ViewAuditLogs)]
    [ProducesResponseType(typeof(UserActivityDashboard), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDashboard(
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null)
    {
        var dashboard = await _userActivityService.GetDashboardAsync(startDate, endDate);
        return Ok(dashboard);
    }

    /// <summary>
    /// Get activity summary for a specific user
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="recentCount">Number of recent activities to include (default: 20)</param>
    /// <returns>User activity summary</returns>
    [HttpGet("user/{userId:guid}")]
    [RequirePermission(Permission.ViewAuditLogs)]
    [ProducesResponseType(typeof(UserActivitySummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserActivity(Guid userId, [FromQuery] int recentCount = 20)
    {
        try
        {
            var summary = await _userActivityService.GetUserActivitySummaryAsync(userId, recentCount);
            return Ok(summary);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Get current user's activity summary
    /// </summary>
    /// <param name="recentCount">Number of recent activities to include (default: 20)</param>
    /// <returns>Current user's activity summary</returns>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserActivitySummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyActivity([FromQuery] int recentCount = 20)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        try
        {
            var summary = await _userActivityService.GetUserActivitySummaryAsync(userId.Value, recentCount);
            return Ok(summary);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Get activities with pagination and filtering
    /// </summary>
    /// <param name="page">Page number (default: 1)</param>
    /// <param name="pageSize">Page size (default: 50)</param>
    /// <param name="userId">Filter by user ID</param>
    /// <param name="action">Filter by action type</param>
    /// <param name="entityType">Filter by entity type</param>
    /// <param name="startDate">Filter by start date</param>
    /// <param name="endDate">Filter by end date</param>
    /// <returns>Paged list of activities</returns>
    [HttpGet]
    [RequirePermission(Permission.ViewAuditLogs)]
    [ProducesResponseType(typeof(PagedResult<UserActivityItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActivities(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? action = null,
        [FromQuery] string? entityType = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null)
    {
        var activities = await _userActivityService.GetActivitiesAsync(
            page, pageSize, userId, action, entityType, startDate, endDate);
        return Ok(activities);
    }

    /// <summary>
    /// Get activities for a specific entity
    /// </summary>
    /// <param name="entityType">Entity type (e.g., "Invoice", "Product")</param>
    /// <param name="entityId">Entity ID</param>
    /// <returns>List of activities for the entity</returns>
    [HttpGet("entity/{entityType}/{entityId}")]
    [RequirePermission(Permission.ViewAuditLogs)]
    [ProducesResponseType(typeof(List<UserActivityItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEntityActivities(string entityType, string entityId)
    {
        var activities = await _userActivityService.GetEntityActivitiesAsync(entityType, entityId);
        return Ok(activities);
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            return userId;
        }
        return null;
    }
}
