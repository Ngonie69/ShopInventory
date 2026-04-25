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
    Task<UserActivitySummary> GetUserActivitySummaryAsync(Guid userId, int recentCount = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get user activity dashboard data
    /// </summary>
    Task<UserActivityDashboard> GetDashboardAsync(DateTime? startDate = null, DateTime? endDate = null, CancellationToken cancellationToken = default);

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
        DateTime? endDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get activities for a specific entity
    /// </summary>
    Task<List<UserActivityItem>> GetEntityActivitiesAsync(string entityType, string entityId, CancellationToken cancellationToken = default);
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

    public async Task<UserActivitySummary> GetUserActivitySummaryAsync(Guid userId, int recentCount = 20, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        var now = DateTime.UtcNow;
        var today = now.Date;
        var weekStart = today.AddDays(-(int)today.DayOfWeek);

        // Query audit logs for this user
        var userLogs = BuildUserAuditQuery(userId, user.Username);

        var totalActions = await userLogs.CountAsync(cancellationToken);
        var periodCounts = await userLogs
            .Where(a => a.Timestamp >= weekStart)
            .Select(a => new
            {
                Bucket = a.Timestamp >= today ? 2 : 1
            })
            .GroupBy(a => a.Bucket)
            .Select(g => new
            {
                Bucket = g.Key,
                Count = g.Count()
            })
            .ToListAsync(cancellationToken);

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
            .ToListAsync(cancellationToken);

        var actionsToday = periodCounts
            .Where(x => x.Bucket == 2)
            .Select(x => x.Count)
            .SingleOrDefault();
        var actionsThisWeek = periodCounts.Sum(x => x.Count);
        var lastActivity = recentActivities.FirstOrDefault();

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

    public async Task<UserActivityDashboard> GetDashboardAsync(DateTime? startDate = null, DateTime? endDate = null, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        startDate ??= now.Date;
        endDate ??= now;

        var query = _context.Set<AuditLog>()
            .AsNoTracking()
            .Where(a => a.Timestamp >= startDate && a.Timestamp <= endDate);

        // Get total actions today
        var actionsToday = await query.CountAsync(cancellationToken);

        var groupedUserActivity = await query
            .Where(a => (a.UserId != null && a.UserId != string.Empty) || a.Username != string.Empty)
            .GroupBy(a => new { a.UserId, a.Username })
            .Select(g => new
            {
                g.Key.UserId,
                g.Key.Username,
                Count = g.Count(),
                LastActivity = g.Max(a => a.Timestamp)
            })
            .ToListAsync(cancellationToken);

        var userIds = groupedUserActivity
            .Where(ua => Guid.TryParse(ua.UserId, out _))
            .Select(ua => Guid.Parse(ua.UserId!))
            .Distinct()
            .ToList();

        var usernames = groupedUserActivity
            .Where(ua => !string.IsNullOrWhiteSpace(ua.Username))
            .Select(ua => ua.Username!)
            .Distinct()
            .ToList();

        var users = await _context.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id) || usernames.Contains(u.Username))
            .ToListAsync(cancellationToken);

        var usersById = users.ToDictionary(u => u.Id);
        var usersByUsername = users
            .GroupBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var mostActiveUsers = groupedUserActivity
            .Select(ua =>
            {
                ShopInventory.Models.User? matchedUser = null;

                if (Guid.TryParse(ua.UserId, out var parsedUserId) && usersById.TryGetValue(parsedUserId, out var idMatch))
                {
                    matchedUser = idMatch;
                }
                else if (!string.IsNullOrWhiteSpace(ua.Username) && usersByUsername.TryGetValue(ua.Username, out var usernameMatch))
                {
                    matchedUser = usernameMatch;
                }

                return matchedUser == null
                    ? null
                    : new
                    {
                        matchedUser.Id,
                        matchedUser.Username,
                        ua.Count,
                        ua.LastActivity
                    };
            })
            .Where(ua => ua != null)
            .GroupBy(ua => ua!.Id)
            .Select(g => new UserActivitySummary
            {
                UserId = g.Key,
                Username = g.First()!.Username,
                TotalActions = g.Sum(x => x!.Count),
                LastActivityAt = g.Max(x => x!.LastActivity)
            })
            .OrderByDescending(x => x.TotalActions)
            .ToList();

        var activeUsersCount = mostActiveUsers.Count;

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
            .ToListAsync(cancellationToken);

        // Get hourly activity
        var hourlyActivity = await query
            .GroupBy(a => a.Timestamp.Hour)
            .Select(g => new HourlyActivityCount
            {
                Hour = g.Key,
                Count = g.Count()
            })
            .OrderBy(x => x.Hour)
            .ToListAsync(cancellationToken);

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
            TotalUsersActive = activeUsersCount,
            TotalActionsToday = actionsToday,
            MostActiveUsers = mostActiveUsers.Take(10).ToList(),
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
        DateTime? endDate = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Set<AuditLog>().AsNoTracking().AsQueryable();

        if (userId.HasValue)
        {
            var username = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId.Value)
                .Select(u => u.Username)
                .SingleOrDefaultAsync(cancellationToken);

            var userIdStr = userId.Value.ToString();
            query = string.IsNullOrWhiteSpace(username)
                ? query.Where(a => a.UserId == userIdStr)
                : query.Where(a => a.UserId == userIdStr || ((a.UserId == null || a.UserId == string.Empty) && a.Username == username));
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

        var totalCount = await query.CountAsync(cancellationToken);

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
            .ToListAsync(cancellationToken);

        return new PagedResult<UserActivityItem>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<List<UserActivityItem>> GetEntityActivitiesAsync(string entityType, string entityId, CancellationToken cancellationToken = default)
    {
        return await _context.Set<AuditLog>()
            .AsNoTracking()
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
            .ToListAsync(cancellationToken);
    }

    private IQueryable<AuditLog> BuildUserAuditQuery(Guid userId, string username)
    {
        var userIdValue = userId.ToString();

        return _context.Set<AuditLog>()
            .AsNoTracking()
            .Where(a => a.UserId == userIdValue || ((a.UserId == null || a.UserId == string.Empty) && a.Username == username));
    }
}
