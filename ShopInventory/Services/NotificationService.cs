using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Hubs;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Interface for notification service
/// </summary>
public interface INotificationService
{
    Task<NotificationDto> CreateNotificationAsync(CreateNotificationRequest request, CancellationToken cancellationToken = default);
    Task<NotificationListResponseDto> GetNotificationsAsync(string? username, string? role, int page = 1, int pageSize = 20, bool unreadOnly = false, string? category = null, CancellationToken cancellationToken = default);
    Task<int> GetUnreadCountAsync(string? username, string? role, CancellationToken cancellationToken = default);
    Task MarkAsReadAsync(string? username, List<int>? notificationIds, CancellationToken cancellationToken = default);
    Task DeleteNotificationAsync(int id, CancellationToken cancellationToken = default);
    Task CleanupExpiredNotificationsAsync(CancellationToken cancellationToken = default);
    Task CreateLowStockAlertAsync(string itemCode, string itemName, decimal currentStock, decimal reorderLevel, CancellationToken cancellationToken = default);
    Task CreateSystemAlertAsync(string title, string message, string type = "Info", CancellationToken cancellationToken = default);
    Task CreateSalesOrderNotificationAsync(string orderNumber, string customerName, decimal docTotal, string source, string? createdByUsername, CancellationToken cancellationToken = default);
}

/// <summary>
/// Notification service implementation
/// </summary>
public class NotificationService : INotificationService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<NotificationService> _logger;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly IPushNotificationService _pushService;

    public NotificationService(ApplicationDbContext context, ILogger<NotificationService> logger, IHubContext<NotificationHub> hubContext, IPushNotificationService pushService)
    {
        _context = context;
        _logger = logger;
        _hubContext = hubContext;
        _pushService = pushService;
    }

    /// <summary>
    /// Create a new notification
    /// </summary>
    public async Task<NotificationDto> CreateNotificationAsync(CreateNotificationRequest request, CancellationToken cancellationToken = default)
    {
        var notification = new Notification
        {
            TargetUsername = request.TargetUsername,
            TargetRole = request.TargetRole,
            Title = request.Title,
            Message = request.Message,
            Type = request.Type,
            Category = request.Category,
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            ActionUrl = request.ActionUrl,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "System",
            ExpiresAt = DateTime.UtcNow.AddDays(30) // Auto-expire after 30 days
        };

        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created notification: {Title} for {Target}", request.Title, request.TargetUsername ?? request.TargetRole ?? "all");

        var dto = MapToDto(notification);

        // Broadcast via SignalR
        try
        {
            if (!string.IsNullOrEmpty(request.TargetUsername))
                await _hubContext.Clients.Group($"user:{request.TargetUsername}").SendAsync("ReceiveNotification", dto);
            else if (!string.IsNullOrEmpty(request.TargetRole))
                await _hubContext.Clients.Group($"role:{request.TargetRole}").SendAsync("ReceiveNotification", dto);
            else
                await _hubContext.Clients.Group("all").SendAsync("ReceiveNotification", dto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast notification via SignalR");
        }

        // Send mobile push notification
        try
        {
            var pushData = new Dictionary<string, string>
            {
                ["notificationId"] = notification.Id.ToString(),
                ["type"] = request.Type,
                ["category"] = request.Category
            };
            if (!string.IsNullOrEmpty(request.ActionUrl))
                pushData["actionUrl"] = request.ActionUrl;
            if (!string.IsNullOrEmpty(request.EntityType))
                pushData["entityType"] = request.EntityType;
            if (!string.IsNullOrEmpty(request.EntityId))
                pushData["entityId"] = request.EntityId;

            if (!string.IsNullOrEmpty(request.TargetUsername))
                await _pushService.SendToUsernameAsync(request.TargetUsername, request.Title, request.Message, pushData, cancellationToken);
            else if (!string.IsNullOrEmpty(request.TargetRole))
                await _pushService.SendToRoleAsync(request.TargetRole, request.Title, request.Message, pushData, cancellationToken);
            else
                await _pushService.SendToAllAsync(request.Title, request.Message, pushData, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send mobile push notification");
        }

        return dto;
    }

    /// <summary>
    /// Get notifications for a user
    /// </summary>
    public async Task<NotificationListResponseDto> GetNotificationsAsync(string? username, string? role, int page = 1, int pageSize = 20, bool unreadOnly = false, string? category = null, CancellationToken cancellationToken = default)
    {
        var query = _context.Notifications
            .Where(n => n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow)
            .Where(n =>
                (n.TargetUsername == null && n.TargetRole == null) || // Broadcast
                n.TargetUsername == username || // Direct to user
                n.TargetRole == role // Role-based
            );

        if (unreadOnly)
        {
            query = query.Where(n => !n.IsRead);
        }

        if (!string.IsNullOrEmpty(category))
        {
            query = query.Where(n => n.Category == category);
        }

        var counts = await query
            .GroupBy(n => 1)
            .Select(g => new { Total = g.Count(), Unread = g.Count(n => !n.IsRead) })
            .FirstOrDefaultAsync(cancellationToken);
        var totalCount = counts?.Total ?? 0;
        var unreadCount = counts?.Unread ?? 0;

        var notifications = (await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken))
            .Select(n => MapToDto(n))
            .ToList();

        return new NotificationListResponseDto
        {
            TotalCount = totalCount,
            UnreadCount = unreadCount,
            Page = page,
            PageSize = pageSize,
            Notifications = notifications
        };
    }

    /// <summary>
    /// Get unread notification count
    /// </summary>
    public async Task<int> GetUnreadCountAsync(string? username, string? role, CancellationToken cancellationToken = default)
    {
        return await _context.Notifications
            .Where(n => n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow)
            .Where(n => !n.IsRead)
            .Where(n =>
                (n.TargetUsername == null && n.TargetRole == null) ||
                n.TargetUsername == username ||
                n.TargetRole == role
            )
            .CountAsync(cancellationToken);
    }

    /// <summary>
    /// Mark notifications as read
    /// </summary>
    public async Task MarkAsReadAsync(string? username, List<int>? notificationIds, CancellationToken cancellationToken = default)
    {
        var query = _context.Notifications.AsTracking().Where(n => !n.IsRead);

        if (notificationIds != null && notificationIds.Any())
        {
            query = query.Where(n => notificationIds.Contains(n.Id));
        }

        var notifications = await query.ToListAsync(cancellationToken);

        foreach (var notification in notifications)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Marked {Count} notifications as read for {User}", notifications.Count, username ?? "user");
    }

    /// <summary>
    /// Delete a notification
    /// </summary>
    public async Task DeleteNotificationAsync(int id, CancellationToken cancellationToken = default)
    {
        var notification = await _context.Notifications.FindAsync(new object[] { id }, cancellationToken);
        if (notification != null)
        {
            _context.Notifications.Remove(notification);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Cleanup expired notifications
    /// </summary>
    public async Task CleanupExpiredNotificationsAsync(CancellationToken cancellationToken = default)
    {
        var expired = await _context.Notifications
            .AsTracking()
            .Where(n => n.ExpiresAt != null && n.ExpiresAt < DateTime.UtcNow)
            .ToListAsync(cancellationToken);

        _context.Notifications.RemoveRange(expired);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Cleaned up {Count} expired notifications", expired.Count);
    }

    /// <summary>
    /// Create a low stock alert notification
    /// </summary>
    public async Task CreateLowStockAlertAsync(string itemCode, string itemName, decimal currentStock, decimal reorderLevel, CancellationToken cancellationToken = default)
    {
        var alertLevel = currentStock <= 0 ? "Critical" : currentStock < reorderLevel / 2 ? "Critical" : "Warning";
        var type = alertLevel == "Critical" ? "Error" : "Warning";

        await CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = $"Low Stock Alert: {itemName}",
            Message = $"Item {itemCode} ({itemName}) has low stock. Current: {currentStock}, Reorder Level: {reorderLevel}",
            Type = type,
            Category = "LowStock",
            EntityType = "Product",
            EntityId = itemCode,
            ActionUrl = $"/products?search={itemCode}",
            TargetRole = "Admin" // Send to all admins
        }, cancellationToken);
    }

    /// <summary>
    /// Create a system alert notification
    /// </summary>
    public async Task CreateSystemAlertAsync(string title, string message, string type = "Info", CancellationToken cancellationToken = default)
    {
        await CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = title,
            Message = message,
            Type = type,
            Category = "System"
        }, cancellationToken);
    }

    /// <summary>
    /// Create a notification when a sales order is successfully posted
    /// </summary>
    public async Task CreateSalesOrderNotificationAsync(string orderNumber, string customerName, decimal docTotal, string source, string? createdByUsername, CancellationToken cancellationToken = default)
    {
        var sourceLabel = source == "Mobile" ? "Mobile App" : "Web";

        await CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = $"New Sales Order: {orderNumber}",
            Message = $"Order {orderNumber} for {customerName} (${docTotal:N2}) submitted from {sourceLabel}" +
                      (createdByUsername != null ? $" by {createdByUsername}" : ""),
            Type = "Success",
            Category = "SalesOrder",
            EntityType = "SalesOrder",
            EntityId = orderNumber,
            ActionUrl = $"/sales-orders",
            TargetRole = "Admin"
        }, cancellationToken);

        // Also notify Cashier role
        await CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = $"New Sales Order: {orderNumber}",
            Message = $"Order {orderNumber} for {customerName} (${docTotal:N2}) submitted from {sourceLabel}" +
                      (createdByUsername != null ? $" by {createdByUsername}" : ""),
            Type = "Success",
            Category = "SalesOrder",
            EntityType = "SalesOrder",
            EntityId = orderNumber,
            ActionUrl = source == "Mobile" ? "/mobile-drafts" : "/sales-orders",
            TargetRole = "Cashier"
        }, cancellationToken);
    }

    private static NotificationDto MapToDto(Notification n) => new()
    {
        Id = n.Id,
        Title = n.Title,
        Message = n.Message,
        Type = n.Type,
        Category = n.Category,
        EntityType = n.EntityType,
        EntityId = n.EntityId,
        ActionUrl = n.ActionUrl,
        IsRead = n.IsRead,
        CreatedAt = n.CreatedAt,
        ReadAt = n.ReadAt,
        CreatedBy = n.CreatedBy
    };
}
