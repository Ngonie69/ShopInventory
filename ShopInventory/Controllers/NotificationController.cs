using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications.Commands.CreateNotification;
using ShopInventory.Features.Notifications.Commands.DeleteNotification;
using ShopInventory.Features.Notifications.Commands.MarkAsRead;
using ShopInventory.Features.Notifications.Queries.GetNotifications;
using ShopInventory.Features.Notifications.Queries.GetUnreadCount;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for notifications
/// </summary>
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class NotificationController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get notifications for the current user
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(NotificationListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotifications(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool unreadOnly = false,
        [FromQuery] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value;
        var roles = User.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = await mediator.Send(new GetNotificationsQuery(page, pageSize, unreadOnly, category, username, roles), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get unread notification count
    /// </summary>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUnreadCount(CancellationToken cancellationToken)
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value;
        var roles = User.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = await mediator.Send(new GetUnreadCountQuery(username, roles), cancellationToken);
        return result.Match(value => Ok(new { unreadCount = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Mark notifications as read
    /// </summary>
    [HttpPost("mark-read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAsRead([FromBody] MarkNotificationsReadRequest request, CancellationToken cancellationToken)
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value;
        var roles = User.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = await mediator.Send(new MarkAsReadCommand(request.NotificationIds ?? [], username, roles), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Notifications marked as read" }), errors => Problem(errors));
    }

    /// <summary>
    /// Create a notification (admin only)
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(NotificationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateNotification([FromBody] CreateNotificationRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateNotificationCommand(request), cancellationToken);
        return result.Match(value => CreatedAtAction(nameof(GetNotifications), value), errors => Problem(errors));
    }

    /// <summary>
    /// Delete a notification
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteNotification(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteNotificationCommand(id), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }
}
