using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Extensions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Hubs;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Interface for notification service
/// </summary>
public interface INotificationService
{
    Task<NotificationDto> CreateNotificationAsync(CreateNotificationRequest request, CancellationToken cancellationToken = default);
    Task<NotificationListResponseDto> GetNotificationsAsync(string? username, IReadOnlyCollection<string>? roles, int page = 1, int pageSize = 20, bool unreadOnly = false, string? category = null, CancellationToken cancellationToken = default);
    Task<int> GetUnreadCountAsync(string? username, IReadOnlyCollection<string>? roles, CancellationToken cancellationToken = default);
    Task MarkAsReadAsync(string? username, IReadOnlyCollection<string>? roles, List<int>? notificationIds, CancellationToken cancellationToken = default);
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

        var dto = MapToDto(notification, request.Data);
        var broadcastAudienceRoles = NotificationAudienceRules.GetBroadcastAudienceRoles(request.Category);

        // Broadcast via SignalR
        try
        {
            if (!string.IsNullOrEmpty(request.TargetUsername))
                await _hubContext.Clients.Group($"user:{request.TargetUsername}").SendAsync("ReceiveNotification", dto);
            else if (!string.IsNullOrEmpty(request.TargetRole))
                await _hubContext.Clients.Group($"role:{request.TargetRole}").SendAsync("ReceiveNotification", dto);
            else if (broadcastAudienceRoles.Length > 0)
                await _hubContext.Clients.Groups(broadcastAudienceRoles.Select(role => $"role:{role}").ToList()).SendAsync("ReceiveNotification", dto);
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
            Guid? targetUserId = request.TargetUserId;
            if (!targetUserId.HasValue && !string.IsNullOrWhiteSpace(request.TargetUsername))
            {
                targetUserId = await _context.Users
                    .AsNoTracking()
                    .WhereUsernameMatches(request.TargetUsername)
                    .Select(user => (Guid?)user.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!targetUserId.HasValue)
                {
                    _logger.LogWarning(
                        "Could not resolve user id for targeted push notification to username {Username}",
                        request.TargetUsername);
                }
            }

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
            if (request.Data != null)
            {
                foreach (var entry in request.Data)
                {
                    pushData[entry.Key] = entry.Value;
                }
            }

            int sentCount;
            if (targetUserId.HasValue)
                sentCount = await _pushService.SendToUserAsync(targetUserId.Value, request.Title, request.Message, pushData, cancellationToken);
            else if (!string.IsNullOrEmpty(request.TargetUsername))
                sentCount = await _pushService.SendToUsernameAsync(request.TargetUsername, request.Title, request.Message, pushData, cancellationToken);
            else if (!string.IsNullOrEmpty(request.TargetRole))
                sentCount = await _pushService.SendToRoleAsync(request.TargetRole, request.Title, request.Message, pushData, cancellationToken);
            else if (broadcastAudienceRoles.Length > 0)
            {
                sentCount = 0;
                foreach (var targetRole in broadcastAudienceRoles)
                {
                    sentCount += await _pushService.SendToRoleAsync(targetRole, request.Title, request.Message, pushData, cancellationToken);
                }
            }
            else
                sentCount = await _pushService.SendToAllAsync(request.Title, request.Message, pushData, cancellationToken);

            if (sentCount == 0)
            {
                _logger.LogWarning(
                    "Push notification {NotificationId} reached no active devices for target {Target}",
                    notification.Id,
                    request.TargetUsername ?? request.TargetRole ?? targetUserId?.ToString() ?? "all");
            }
            else
            {
                _logger.LogInformation(
                    "Push notification {NotificationId} sent to {SentCount} device(s)",
                    notification.Id,
                    sentCount);
            }
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
    public async Task<NotificationListResponseDto> GetNotificationsAsync(string? username, IReadOnlyCollection<string>? roles, int page = 1, int pageSize = 20, bool unreadOnly = false, string? category = null, CancellationToken cancellationToken = default)
    {
        var query = BuildVisibleNotificationsQuery(username, roles);

        if (unreadOnly)
        {
            query = query.Where(n => !n.IsRead);
        }

        if (!string.IsNullOrEmpty(category))
        {
            query = query.Where(n => n.Category == category);
        }

        var counts = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalCount = g.Count(),
                UnreadCount = unreadOnly ? g.Count() : g.Count(n => !n.IsRead)
            })
            .FirstOrDefaultAsync(cancellationToken);

        var notifications = (await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken))
            .Select(n => MapToDto(n))
            .ToList();

        return new NotificationListResponseDto
        {
            TotalCount = counts?.TotalCount ?? 0,
            UnreadCount = counts?.UnreadCount ?? 0,
            Page = page,
            PageSize = pageSize,
            Notifications = notifications
        };
    }

    /// <summary>
    /// Get unread notification count
    /// </summary>
    public async Task<int> GetUnreadCountAsync(string? username, IReadOnlyCollection<string>? roles, CancellationToken cancellationToken = default)
    {
        return await BuildVisibleNotificationsQuery(username, roles)
            .Where(n => !n.IsRead)
            .CountAsync(cancellationToken);
    }

    /// <summary>
    /// Mark notifications as read
    /// </summary>
    public async Task MarkAsReadAsync(string? username, IReadOnlyCollection<string>? roles, List<int>? notificationIds, CancellationToken cancellationToken = default)
    {
        var query = BuildVisibleNotificationsQuery(username, roles, asTracking: true)
            .Where(n => !n.IsRead);

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

    private IQueryable<Notification> BuildVisibleNotificationsQuery(
        string? username,
        IReadOnlyCollection<string>? roles,
        bool asTracking = false)
    {
        var normalizedRoles = NotificationAudienceRules.NormalizeRoles(roles);
        var hasUsername = !string.IsNullOrWhiteSpace(username);
        var hasRoles = normalizedRoles.Length > 0;
        var canSeeSalesBroadcasts = NotificationAudienceRules.HasAnyRole(normalizedRoles, NotificationAudienceRules.SalesAudienceRoles);
        var canSeeInvoiceBroadcasts = NotificationAudienceRules.HasAnyRole(normalizedRoles, NotificationAudienceRules.InvoiceAudienceRoles);
        var canSeePaymentBroadcasts = NotificationAudienceRules.HasAnyRole(normalizedRoles, NotificationAudienceRules.PaymentAudienceRoles);
        var canSeeInventoryBroadcasts = NotificationAudienceRules.HasAnyRole(normalizedRoles, NotificationAudienceRules.InventoryAudienceRoles);
        var canSeePurchasingBroadcasts = NotificationAudienceRules.HasAnyRole(normalizedRoles, NotificationAudienceRules.PurchasingAudienceRoles);
        var canSeePodBroadcasts = NotificationAudienceRules.HasAnyRole(normalizedRoles, NotificationAudienceRules.PodAudienceRoles);
        var now = DateTime.UtcNow;

        var query = asTracking ? _context.Notifications.AsTracking() : _context.Notifications.AsNoTracking();
        query = query.Where(n => n.ExpiresAt == null || n.ExpiresAt > now);

        if (NotificationAudienceRules.IsAdmin(normalizedRoles))
        {
            return query.Where(n =>
                (n.TargetUsername == null && n.TargetRole == null) ||
                (hasUsername && n.TargetUsername == username) ||
                (hasRoles && n.TargetRole != null && normalizedRoles.Contains(n.TargetRole)));
        }

        return query.Where(n =>
            (hasUsername && n.TargetUsername == username) ||
            (hasRoles && n.TargetRole != null && normalizedRoles.Contains(n.TargetRole)) ||
            (n.TargetUsername == null && n.TargetRole == null && (
                NotificationAudienceRules.GlobalBroadcastCategories.Contains(n.Category) ||
                (canSeeSalesBroadcasts && NotificationAudienceRules.SalesBroadcastCategories.Contains(n.Category)) ||
                (canSeeInvoiceBroadcasts && NotificationAudienceRules.InvoiceBroadcastCategories.Contains(n.Category)) ||
                (canSeePaymentBroadcasts && NotificationAudienceRules.PaymentBroadcastCategories.Contains(n.Category)) ||
                (canSeeInventoryBroadcasts && NotificationAudienceRules.InventoryBroadcastCategories.Contains(n.Category)) ||
                (canSeePurchasingBroadcasts && NotificationAudienceRules.PurchasingBroadcastCategories.Contains(n.Category)) ||
                (canSeePodBroadcasts && NotificationAudienceRules.PodBroadcastCategories.Contains(n.Category)))));
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
        var isMobileOrder = string.Equals(source, "Mobile", StringComparison.OrdinalIgnoreCase);
        var sourceLabel = isMobileOrder ? "Mobile App" : "Web";
        var actionUrl = isMobileOrder ? "/mobile-drafts" : "/sales-orders";
        var title = $"New Sales Order: {orderNumber}";
        var message = $"Order {orderNumber} for {customerName} (${docTotal:N2}) submitted from {sourceLabel}" +
                      (createdByUsername != null ? $" by {createdByUsername}" : "");

        await CreateSalesOrderRoleNotificationIfMissingAsync(orderNumber, title, message, actionUrl, "Admin", cancellationToken);
        await CreateSalesOrderRoleNotificationIfMissingAsync(orderNumber, title, message, actionUrl, "Cashier", cancellationToken);
    }

    private async Task CreateSalesOrderRoleNotificationIfMissingAsync(
        string orderNumber,
        string title,
        string message,
        string actionUrl,
        string targetRole,
        CancellationToken cancellationToken)
    {
        var exists = await _context.Notifications
            .AsNoTracking()
            .AnyAsync(n => n.Category == "SalesOrder" &&
                           n.EntityType == "SalesOrder" &&
                           n.EntityId == orderNumber &&
                           n.TargetRole == targetRole &&
                           n.Title == title,
                cancellationToken);

        if (exists)
        {
            _logger.LogDebug(
                "Skipping duplicate sales order notification for {OrderNumber} and role {Role}",
                orderNumber,
                targetRole);
            return;
        }

        await CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = title,
            Message = message,
            Type = "Success",
            Category = "SalesOrder",
            EntityType = "SalesOrder",
            EntityId = orderNumber,
            ActionUrl = actionUrl,
            TargetRole = targetRole
        }, cancellationToken);
    }

    private static NotificationDto MapToDto(Notification n, Dictionary<string, string>? data = null) => new()
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
        CreatedBy = n.CreatedBy,
        Data = data == null ? null : new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase)
    };
}
