using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Service for user activity tracking and dashboard
/// </summary>
public interface IUserActivityService
{
    /// <summary>
    /// Get activity summary for a specific user
    /// </summary>
    Task<UserActivitySummary> GetUserActivitySummaryAsync(Guid userId, int recentCount = 20);

    /// <summary>
    /// Get user activity dashboard data
    /// </summary>
    Task<UserActivityDashboard> GetDashboardAsync(DateTime? startDate = null, DateTime? endDate = null);

    /// <summary>
    /// Get recent activities with pagination
    /// </summary>
    Task<PagedResult<UserActivityItem>> GetActivitiesAsync(
        int page = 1,
        int pageSize = 50,
        Guid? userId = null,
        string? action = null,
        string? entityType = null,
        DateTime? startDate = null,
        DateTime? endDate = null);

    /// <summary>
    /// Get activities for a specific entity
    /// </summary>
    Task<List<UserActivityItem>> GetEntityActivitiesAsync(string entityType, string entityId);
}

/// <summary>
/// Implementation of user activity service
/// </summary>
public class UserActivityService : IUserActivityService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<UserActivityService> _logger;

    public UserActivityService(ApplicationDbContext context, ILogger<UserActivityService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<UserActivitySummary> GetUserActivitySummaryAsync(Guid userId, int recentCount = 20)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        var now = DateTime.UtcNow;
        var today = now.Date;
        var weekStart = today.AddDays(-(int)today.DayOfWeek);

        // Query audit logs for this user
        var userLogs = _context.Set<AuditLog>()
            .Where(a => a.UserId == userId.ToString());

        var totalActions = await userLogs.CountAsync();
        var actionsToday = await userLogs.Where(a => a.Timestamp >= today).CountAsync();
        var actionsThisWeek = await userLogs.Where(a => a.Timestamp >= weekStart).CountAsync();

        var lastActivity = await userLogs
            .OrderByDescending(a => a.Timestamp)
            .FirstOrDefaultAsync();

        var recentActivities = await userLogs
            .OrderByDescending(a => a.Timestamp)
            .Take(recentCount)
            .Select(a => new UserActivityItem
            {
                Id = a.Id,
                Action = a.Action,
                EntityType = a.EntityType,
                EntityId = a.EntityId,
                Details = a.Details,
                PageUrl = a.PageUrl,
                IsSuccess = a.IsSuccess,
                Timestamp = a.Timestamp
            })
            .ToListAsync();

        return new UserActivitySummary
        {
            UserId = userId,
            Username = user.Username,
            TotalActions = totalActions,
            ActionsToday = actionsToday,
            ActionsThisWeek = actionsThisWeek,
            LastActivityAt = lastActivity?.Timestamp,
            LastAction = lastActivity?.Action,
            RecentActivities = recentActivities
        };
    }

    public async Task<UserActivityDashboard> GetDashboardAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        var now = DateTime.UtcNow;
        startDate ??= now.Date;
        endDate ??= now;

        var query = _context.Set<AuditLog>()
            .Where(a => a.Timestamp >= startDate && a.Timestamp <= endDate);

        // Get total active users
        var activeUserIds = await query
            .Where(a => a.UserId != null)
            .Select(a => a.UserId)
            .Distinct()
            .CountAsync();

        // Get total actions today
        var actionsToday = await query.CountAsync();

        // Get most active users
        var userActivity = await query
            .Where(a => a.UserId != null)
            .GroupBy(a => a.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Count = g.Count(),
                LastActivity = g.Max(a => a.Timestamp)
            })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync();

        var mostActiveUsers = new List<UserActivitySummary>();
        foreach (var ua in userActivity)
        {
            if (Guid.TryParse(ua.UserId, out var userId))
            {
                var user = await _context.Users.FindAsync(userId);
                if (user != null)
                {
                    mostActiveUsers.Add(new UserActivitySummary
                    {
                        UserId = userId,
                        Username = user.Username,
                        TotalActions = ua.Count,
                        LastActivityAt = ua.LastActivity
                    });
                }
            }
        }

        // Get action breakdown
        var actionBreakdown = await query
            .GroupBy(a => a.Action)
            .Select(g => new ActionTypeCount
            {
                Action = g.Key,
                Count = g.Count()
            })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync();

        // Get hourly activity
        var hourlyActivity = await query
            .GroupBy(a => a.Timestamp.Hour)
            .Select(g => new HourlyActivityCount
            {
                Hour = g.Key,
                Count = g.Count()
            })
            .OrderBy(x => x.Hour)
            .ToListAsync();

        // Fill in missing hours with 0
        var allHours = Enumerable.Range(0, 24)
            .Select(h => new HourlyActivityCount
            {
                Hour = h,
                Count = hourlyActivity.FirstOrDefault(x => x.Hour == h)?.Count ?? 0
            })
            .ToList();

        return new UserActivityDashboard
        {
            TotalUsersActive = activeUserIds,
            TotalActionsToday = actionsToday,
            MostActiveUsers = mostActiveUsers,
            ActionBreakdown = actionBreakdown,
            HourlyActivity = allHours
        };
    }

    public async Task<PagedResult<UserActivityItem>> GetActivitiesAsync(
        int page = 1,
        int pageSize = 50,
        Guid? userId = null,
        string? action = null,
        string? entityType = null,
        DateTime? startDate = null,
        DateTime? endDate = null)
    {
        var query = _context.Set<AuditLog>().AsQueryable();

        if (userId.HasValue)
        {
            var userIdStr = userId.Value.ToString();
            query = query.Where(a => a.UserId == userIdStr);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action.Contains(action));
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (startDate.HasValue)
        {
            query = query.Where(a => a.Timestamp >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            query = query.Where(a => a.Timestamp <= endDate.Value);
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new UserActivityItem
            {
                Id = a.Id,
                Action = a.Action,
                EntityType = a.EntityType,
                EntityId = a.EntityId,
                Details = a.Details,
                PageUrl = a.PageUrl,
                IsSuccess = a.IsSuccess,
                Timestamp = a.Timestamp
            })
            .ToListAsync();

        return new PagedResult<UserActivityItem>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<List<UserActivityItem>> GetEntityActivitiesAsync(string entityType, string entityId)
    {
        return await _context.Set<AuditLog>()
            .Where(a => a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.Timestamp)
            .Take(100)
            .Select(a => new UserActivityItem
            {
                Id = a.Id,
                Action = a.Action,
                EntityType = a.EntityType,
                EntityId = a.EntityId,
                Details = a.Details,
                PageUrl = a.PageUrl,
                IsSuccess = a.IsSuccess,
                Timestamp = a.Timestamp
            })
            .ToListAsync();
    }
}
