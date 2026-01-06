using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;
using System.Security.Claims;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for notifications
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class NotificationController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly ILogger<NotificationController> _logger;

    public NotificationController(INotificationService notificationService, ILogger<NotificationController> logger)
    {
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Get notifications for the current user
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(NotificationListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotifications(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value;
        var role = User.FindFirst(ClaimTypes.Role)?.Value;

        var notifications = await _notificationService.GetNotificationsAsync(username, role, page, pageSize, unreadOnly, cancellationToken);
        return Ok(notifications);
    }

    /// <summary>
    /// Get unread notification count
    /// </summary>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUnreadCount(CancellationToken cancellationToken)
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value;
        var role = User.FindFirst(ClaimTypes.Role)?.Value;

        var count = await _notificationService.GetUnreadCountAsync(username, role, cancellationToken);
        return Ok(new { unreadCount = count });
    }

    /// <summary>
    /// Mark notifications as read
    /// </summary>
    [HttpPost("mark-read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAsRead([FromBody] MarkNotificationsReadRequest request, CancellationToken cancellationToken)
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value;

        await _notificationService.MarkAsReadAsync(username, request.NotificationIds, cancellationToken);
        return Ok(new { Message = "Notifications marked as read" });
    }

    /// <summary>
    /// Create a notification (admin only)
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(NotificationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateNotification([FromBody] CreateNotificationRequest request, CancellationToken cancellationToken)
    {
        var notification = await _notificationService.CreateNotificationAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetNotifications), notification);
    }

    /// <summary>
    /// Delete a notification
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteNotification(int id, CancellationToken cancellationToken)
    {
        await _notificationService.DeleteNotificationAsync(id, cancellationToken);
        return NoContent();
    }
}

/// <summary>
/// Controller for sync status and offline queue
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class SyncController : ControllerBase
{
    private readonly ISyncStatusService _syncStatusService;
    private readonly IOfflineQueueService _offlineQueueService;
    private readonly ILogger<SyncController> _logger;

    public SyncController(
        ISyncStatusService syncStatusService,
        IOfflineQueueService offlineQueueService,
        ILogger<SyncController> logger)
    {
        _syncStatusService = syncStatusService;
        _offlineQueueService = offlineQueueService;
        _logger = logger;
    }

    /// <summary>
    /// Get sync status dashboard
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(SyncStatusDashboardDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSyncStatus(CancellationToken cancellationToken)
    {
        var status = await _syncStatusService.GetSyncStatusDashboardAsync(cancellationToken);
        return Ok(status);
    }

    /// <summary>
    /// Check SAP connection
    /// </summary>
    [HttpGet("sap-connection")]
    [ProducesResponseType(typeof(SapConnectionStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckSapConnection(CancellationToken cancellationToken)
    {
        var status = await _syncStatusService.CheckSapConnectionAsync(cancellationToken);
        return Ok(status);
    }

    /// <summary>
    /// Get health summary
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(SyncHealthSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHealthSummary(CancellationToken cancellationToken)
    {
        var health = await _syncStatusService.GetHealthSummaryAsync(cancellationToken);
        return Ok(health);
    }

    /// <summary>
    /// Get offline queue status
    /// </summary>
    [HttpGet("queue")]
    [ProducesResponseType(typeof(OfflineQueueStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueStatus(CancellationToken cancellationToken)
    {
        var status = await _offlineQueueService.GetQueueStatusAsync(cancellationToken);
        return Ok(status);
    }

    /// <summary>
    /// Retry a failed transaction
    /// </summary>
    [HttpPost("queue/{id}/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetryTransaction(int id, CancellationToken cancellationToken)
    {
        var success = await _offlineQueueService.RetryTransactionAsync(id, cancellationToken);
        if (!success)
        {
            return NotFound(new { Message = "Transaction not found or not in failed state" });
        }
        return Ok(new { Message = "Transaction retry initiated" });
    }

    /// <summary>
    /// Cancel a pending transaction
    /// </summary>
    [HttpPost("queue/{id}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelTransaction(int id, CancellationToken cancellationToken)
    {
        var success = await _offlineQueueService.CancelTransactionAsync(id, cancellationToken);
        if (!success)
        {
            return NotFound(new { Message = "Transaction not found or already completed" });
        }
        return Ok(new { Message = "Transaction cancelled" });
    }

    /// <summary>
    /// Process pending queue items (admin only)
    /// </summary>
    [HttpPost("queue/process")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProcessQueue(CancellationToken cancellationToken)
    {
        await _offlineQueueService.ProcessQueueAsync(cancellationToken);
        return Ok(new { Message = "Queue processing initiated" });
    }
}

/// <summary>
/// Controller for email operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class EmailController : ControllerBase
{
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailController> _logger;

    public EmailController(IEmailService emailService, ILogger<EmailController> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    /// <summary>
    /// Send a test email
    /// </summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(EmailSentResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SendTestEmail([FromBody] TestEmailRequest request, CancellationToken cancellationToken)
    {
        var result = await _emailService.TestEmailConfigurationAsync(request.ToEmail, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Send an email
    /// </summary>
    [HttpPost("send")]
    [ProducesResponseType(typeof(EmailSentResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SendEmail([FromBody] SendEmailRequest request, CancellationToken cancellationToken)
    {
        var result = await _emailService.SendEmailAsync(request, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Queue an email for later sending
    /// </summary>
    [HttpPost("queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> QueueEmail([FromBody] SendEmailRequest request, [FromQuery] string? category, CancellationToken cancellationToken)
    {
        await _emailService.QueueEmailAsync(request, category, cancellationToken);
        return Ok(new { Message = "Email queued successfully" });
    }

    /// <summary>
    /// Process email queue
    /// </summary>
    [HttpPost("process-queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProcessEmailQueue(CancellationToken cancellationToken)
    {
        await _emailService.ProcessEmailQueueAsync(cancellationToken);
        return Ok(new { Message = "Email queue processing initiated" });
    }
}
