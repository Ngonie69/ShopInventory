using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using static ShopInventory.Web.Components.Pages.UserActivity;

namespace ShopInventory.Web.Services;

public interface IAuditService
{
    /// <summary>
    /// Central Africa Time (CAT) timezone - UTC+2
    /// </summary>
    static TimeZoneInfo CatTimeZone => TimeZoneInfo.CreateCustomTimeZone("CAT", TimeSpan.FromHours(2), "Central Africa Time", "Central Africa Time");

    /// <summary>
    /// Convert UTC DateTime to CAT timezone
    /// </summary>
    static DateTime ToCAT(DateTime utcDateTime) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc), CatTimeZone);

    /// <summary>
    /// Convert CAT local DateTime to UTC (returns DateTime with Kind=Utc)
    /// </summary>
    static DateTime ToUTC(DateTime catDateTime)
    {
        // Treat the input as CAT time (UTC+2) and convert to UTC
        var utcTime = DateTime.SpecifyKind(catDateTime, DateTimeKind.Unspecified);
        var converted = TimeZoneInfo.ConvertTimeToUtc(utcTime, CatTimeZone);
        return DateTime.SpecifyKind(converted, DateTimeKind.Utc);
    }

    Task LogAsync(string action, string username, string userRole, string? entityType = null,
        string? entityId = null, string? details = null, string? pageUrl = null,
        bool isSuccess = true, string? errorMessage = null);

    // Simplified version for easy logging
    Task LogAsync(string action, string? entityType = null, string? entityId = null);

    Task<List<AuditLog>> GetLogsAsync(DateTime? fromDate = null, DateTime? toDate = null,
        string? username = null, string? action = null, int page = 1, int pageSize = 50);

    Task<int> GetLogCountAsync(DateTime? fromDate = null, DateTime? toDate = null,
        string? username = null, string? action = null);

    Task<List<string>> GetDistinctActionsAsync();
    Task<List<string>> GetDistinctUsersAsync();
    Task CleanupOldLogsAsync(int retentionDays);

    // Activity Dashboard methods
    Task<ActivityStats> GetActivityStatsAsync(DateTime startDate, DateTime endDate);
    Task<List<UserActivitySummary>> GetMostActiveUsersAsync(DateTime startDate, DateTime endDate, int count = 10);
    Task<List<ActionCount>> GetActionBreakdownAsync(DateTime startDate, DateTime endDate);
    Task<List<HourlyCount>> GetHourlyActivityAsync(DateTime startDate, DateTime endDate);
    Task<ActivityLogResult> GetActivityLogsAsync(DateTime startDate, DateTime endDate, string? username = null, string? action = null, int page = 1, int pageSize = 50);
    Task<List<string>> GetUniqueUsersAsync(DateTime startDate, DateTime endDate);
    Task<List<string>> GetUniqueActionsAsync(DateTime startDate, DateTime endDate);
}

public class ActivityStats
{
    public int ActiveUsers { get; set; }
    public int TotalActions { get; set; }
    public int LoginCount { get; set; }
    public int FailedActions { get; set; }
}

public class ActivityLogResult
{
    public List<ActivityLog> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

public class AuditService : IAuditService
{
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IDbContextFactory<WebAppDbContext> dbContextFactory, ILogger<AuditService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task LogAsync(string action, string username, string userRole, string? entityType = null,
        string? entityId = null, string? details = null, string? pageUrl = null,
        bool isSuccess = true, string? errorMessage = null)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var log = new AuditLog
            {
                Action = action,
                Username = username,
                UserRole = userRole,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                PageUrl = pageUrl,
                IsSuccess = isSuccess,
                ErrorMessage = errorMessage,
                Timestamp = DateTime.UtcNow
            };

            db.AuditLogs.Add(log);
            await db.SaveChangesAsync();

            _logger.LogDebug("Audit log created: {Action} by {Username}", action, username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create audit log for action {Action}", action);
        }
    }

    public async Task<List<AuditLog>> GetLogsAsync(DateTime? fromDate = null, DateTime? toDate = null,
        string? username = null, string? action = null, int page = 1, int pageSize = 50)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var query = db.AuditLogs.AsQueryable();

        // Convert CAT dates to UTC for database query
        if (fromDate.HasValue)
        {
            var fromUtc = IAuditService.ToUTC(fromDate.Value.Date);
            query = query.Where(l => l.Timestamp >= fromUtc);
        }

        if (toDate.HasValue)
        {
            var toUtc = IAuditService.ToUTC(toDate.Value.Date.AddDays(1));
            query = query.Where(l => l.Timestamp < toUtc);
        }

        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(l => l.Username.Contains(username));

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(l => l.Action == action);

        return await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<int> GetLogCountAsync(DateTime? fromDate = null, DateTime? toDate = null,
        string? username = null, string? action = null)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var query = db.AuditLogs.AsQueryable();

        // Convert CAT dates to UTC for database query
        if (fromDate.HasValue)
        {
            var fromUtc = IAuditService.ToUTC(fromDate.Value.Date);
            query = query.Where(l => l.Timestamp >= fromUtc);
        }

        if (toDate.HasValue)
        {
            var toUtc = IAuditService.ToUTC(toDate.Value.Date.AddDays(1));
            query = query.Where(l => l.Timestamp < toUtc);
        }

        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(l => l.Username.Contains(username));

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(l => l.Action == action);

        return await query.CountAsync();
    }

    public async Task<List<string>> GetDistinctActionsAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.AuditLogs
            .Select(l => l.Action)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync();
    }

    public async Task<List<string>> GetDistinctUsersAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        return await db.AuditLogs
            .Select(l => l.Username)
            .Distinct()
            .OrderBy(u => u)
            .ToListAsync();
    }

    public async Task CleanupOldLogsAsync(int retentionDays)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
            var deletedCount = await db.AuditLogs
                .Where(l => l.Timestamp < cutoffDate)
                .ExecuteDeleteAsync();

            _logger.LogInformation("Cleaned up {Count} audit logs older than {Days} days", deletedCount, retentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old audit logs");
        }
    }

    public async Task LogAsync(string action, string? entityType = null, string? entityId = null)
    {
        // Simplified logging with default values
        await LogAsync(action, "System", "System", entityType, entityId);
    }

    public async Task<ActivityStats> GetActivityStatsAsync(DateTime startDate, DateTime endDate)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var query = db.AuditLogs.Where(l => l.Timestamp >= startDate && l.Timestamp <= endDate);

        return new ActivityStats
        {
            ActiveUsers = await query.Select(l => l.Username).Distinct().CountAsync(),
            TotalActions = await query.CountAsync(),
            LoginCount = await query.Where(l => l.Action.Contains("Login")).CountAsync(),
            FailedActions = await query.Where(l => !l.IsSuccess).CountAsync()
        };
    }

    public async Task<List<UserActivitySummary>> GetMostActiveUsersAsync(DateTime startDate, DateTime endDate, int count = 10)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.AuditLogs
            .Where(l => l.Timestamp >= startDate && l.Timestamp <= endDate)
            .GroupBy(l => l.Username)
            .Select(g => new UserActivitySummary
            {
                Username = g.Key,
                ActionCount = g.Count(),
                LastActivityAt = g.Max(l => l.Timestamp)
            })
            .OrderByDescending(x => x.ActionCount)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<ActionCount>> GetActionBreakdownAsync(DateTime startDate, DateTime endDate)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.AuditLogs
            .Where(l => l.Timestamp >= startDate && l.Timestamp <= endDate)
            .GroupBy(l => l.Action)
            .Select(g => new ActionCount
            {
                Action = g.Key,
                Count = g.Count()
            })
            .OrderByDescending(x => x.Count)
            .ToListAsync();
    }

    public async Task<List<HourlyCount>> GetHourlyActivityAsync(DateTime startDate, DateTime endDate)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var hourlyData = await db.AuditLogs
            .Where(l => l.Timestamp >= startDate && l.Timestamp <= endDate)
            .GroupBy(l => l.Timestamp.Hour)
            .Select(g => new HourlyCount
            {
                Hour = g.Key,
                Count = g.Count()
            })
            .ToListAsync();

        // Fill in missing hours with 0
        return Enumerable.Range(0, 24)
            .Select(h => new HourlyCount
            {
                Hour = h,
                Count = hourlyData.FirstOrDefault(x => x.Hour == h)?.Count ?? 0
            })
            .ToList();
    }

    public async Task<ActivityLogResult> GetActivityLogsAsync(DateTime startDate, DateTime endDate, string? username = null, string? action = null, int page = 1, int pageSize = 50)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var query = db.AuditLogs.Where(l => l.Timestamp >= startDate && l.Timestamp <= endDate);

        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(l => l.Username == username);

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(l => l.Action == action);

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new ActivityLog
            {
                Username = l.Username,
                Action = l.Action,
                EntityType = l.EntityType,
                EntityId = l.EntityId,
                Details = l.Details,
                IsSuccess = l.IsSuccess,
                Timestamp = l.Timestamp
            })
            .ToListAsync();

        return new ActivityLogResult
        {
            Items = items,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        };
    }

    public async Task<List<string>> GetUniqueUsersAsync(DateTime startDate, DateTime endDate)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.AuditLogs
            .Where(l => l.Timestamp >= startDate && l.Timestamp <= endDate)
            .Select(l => l.Username)
            .Distinct()
            .OrderBy(u => u)
            .ToListAsync();
    }

    public async Task<List<string>> GetUniqueActionsAsync(DateTime startDate, DateTime endDate)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.AuditLogs
            .Where(l => l.Timestamp >= startDate && l.Timestamp <= endDate)
            .Select(l => l.Action)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync();
    }
}
