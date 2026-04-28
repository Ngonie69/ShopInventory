using Microsoft.AspNetCore.Components.Authorization;
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

    // Simplified version with success/failure info (auto-resolves current user)
    Task LogAsync(string action, string? entityType, string? entityId, string? details, bool isSuccess, string? errorMessage = null);

    Task<List<AuditLog>> GetLogsAsync(DateTime? fromDate = null, DateTime? toDate = null,
        string? username = null, string? action = null, int page = 1, int pageSize = 50);

    Task<AuditLogPageResult> GetLogPageAsync(DateTime? fromDate = null, DateTime? toDate = null,
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

public class AuditLogPageResult
{
    public List<AuditLog> Items { get; set; } = new();
    public bool HasMore { get; set; }
}

internal sealed class ApiPagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

internal sealed class ApiAuditLogItem
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? UserRole { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? PageUrl { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; }
}

internal sealed class ApiAuditFilterOptions
{
    public List<string> Users { get; set; } = new();
    public List<string> Actions { get; set; } = new();
}

public class AuditService : IAuditService
{
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AuditService> _logger;
    private readonly AuthenticationStateProvider _authStateProvider;

    private const int ApiActivityPageSize = 500;
    private const int LocalAuditPageSize = 500;
    private static readonly TimeSpan AuditDeduplicationWindow = TimeSpan.FromSeconds(5);

    public AuditService(IDbContextFactory<WebAppDbContext> dbContextFactory, HttpClient httpClient, ILogger<AuditService> logger, AuthenticationStateProvider authStateProvider)
    {
        _dbContextFactory = dbContextFactory;
        _httpClient = httpClient;
        _logger = logger;
        _authStateProvider = authStateProvider;
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
        var pageResult = await GetLogPageAsync(fromDate, toDate, username, action, page, pageSize);
        return pageResult.Items;
    }

    public async Task<AuditLogPageResult> GetLogPageAsync(DateTime? fromDate = null, DateTime? toDate = null,
        string? username = null, string? action = null, int page = 1, int pageSize = 50)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Max(1, pageSize);
        var skipCount = checked((safePage - 1) * safePageSize);
        var (startDateUtc, endDateUtc) = NormalizeAuditDateRange(fromDate, toDate);

        var (items, hasMore, _) = await QueryMergedAuditLogsAsync(
            startDateUtc,
            endDateUtc,
            username,
            action,
            skipCount,
            safePageSize,
            includeTotalCount: false);

        return new AuditLogPageResult
        {
            Items = items,
            HasMore = hasMore
        };
    }

    public async Task<int> GetLogCountAsync(DateTime? fromDate = null, DateTime? toDate = null,
        string? username = null, string? action = null)
    {
        var (startDateUtc, endDateUtc) = NormalizeAuditDateRange(fromDate, toDate);
        var (_, _, totalCount) = await QueryMergedAuditLogsAsync(
            startDateUtc,
            endDateUtc,
            username,
            action,
            skipCount: 0,
            takeCount: 0,
            includeTotalCount: true);

        return totalCount;
    }

    public async Task<List<string>> GetDistinctActionsAsync()
    {
        var localActionsTask = GetLocalDistinctActionsAsync();
        var apiOptionsTask = GetApiAuditFilterOptionsAsync();

        await Task.WhenAll(localActionsTask, apiOptionsTask);

        return localActionsTask.Result
            .Concat(apiOptionsTask.Result.Actions)
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(action => action, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<string>> GetDistinctUsersAsync()
    {
        var localUsersTask = GetLocalDistinctUsersAsync();
        var apiOptionsTask = GetApiAuditFilterOptionsAsync();

        await Task.WhenAll(localUsersTask, apiOptionsTask);

        return localUsersTask.Result
            .Concat(apiOptionsTask.Result.Users)
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        // Auto-resolve current user from auth state
        var username = "System";
        var role = "System";
        try
        {
            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            if (authState.User?.Identity?.IsAuthenticated == true)
            {
                username = authState.User.Identity.Name ?? "Unknown";
                role = authState.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "User";
            }
        }
        catch { /* fallback to System */ }
        await LogAsync(action, username, role, entityType, entityId);
    }

    public async Task LogAsync(string action, string? entityType, string? entityId, string? details, bool isSuccess, string? errorMessage = null)
    {
        var username = "System";
        var role = "System";
        try
        {
            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            if (authState.User?.Identity?.IsAuthenticated == true)
            {
                username = authState.User.Identity.Name ?? "Unknown";
                role = authState.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "User";
            }
        }
        catch { /* fallback to System */ }
        await LogAsync(action, username, role, entityType, entityId, details, null, isSuccess, errorMessage);
    }

    public async Task<ActivityStats> GetActivityStatsAsync(DateTime startDate, DateTime endDate)
    {
        var logs = await GetMergedAuditLogsAsync(startDate, endDate);

        return new ActivityStats
        {
            ActiveUsers = logs.Select(l => l.Username).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            TotalActions = logs.Count,
            LoginCount = logs.Count(l => l.Action.Contains("Login", StringComparison.OrdinalIgnoreCase)),
            FailedActions = logs.Count(l => !l.IsSuccess)
        };
    }

    public async Task<List<UserActivitySummary>> GetMostActiveUsersAsync(DateTime startDate, DateTime endDate, int count = 10)
    {
        return (await GetMergedAuditLogsAsync(startDate, endDate))
            .Where(l => !string.IsNullOrWhiteSpace(l.Username))
            .GroupBy(l => l.Username, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UserActivitySummary
            {
                Username = g.Key,
                ActionCount = g.Count(),
                LastActivityAt = g.Max(l => l.Timestamp)
            })
            .OrderByDescending(x => x.ActionCount)
            .Take(count)
            .ToList();
    }

    public async Task<List<ActionCount>> GetActionBreakdownAsync(DateTime startDate, DateTime endDate)
    {
        return (await GetMergedAuditLogsAsync(startDate, endDate))
            .GroupBy(l => l.Action)
            .Select(g => new ActionCount
            {
                Action = g.Key,
                Count = g.Count()
            })
            .OrderByDescending(x => x.Count)
            .ToList();
    }

    public async Task<List<HourlyCount>> GetHourlyActivityAsync(DateTime startDate, DateTime endDate)
    {
        var hourlyData = (await GetMergedAuditLogsAsync(startDate, endDate))
            .GroupBy(l => l.Timestamp.Hour)
            .Select(g => new HourlyCount
            {
                Hour = g.Key,
                Count = g.Count()
            })
            .ToList();

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
        var filteredLogs = (await GetMergedAuditLogsAsync(startDate, endDate))
            .Where(l => string.IsNullOrWhiteSpace(username) || string.Equals(l.Username, username, StringComparison.OrdinalIgnoreCase))
            .Where(l => string.IsNullOrWhiteSpace(action) || string.Equals(l.Action, action, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var totalCount = filteredLogs.Count;

        var items = filteredLogs
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(MapToActivityLog)
            .ToList();

        return new ActivityLogResult
        {
            Items = items,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        };
    }

    public async Task<List<string>> GetUniqueUsersAsync(DateTime startDate, DateTime endDate)
    {
        return (await GetMergedAuditLogsAsync(startDate, endDate))
            .Select(l => l.Username)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u)
            .ToList();
    }

    public async Task<List<string>> GetUniqueActionsAsync(DateTime startDate, DateTime endDate)
    {
        return (await GetMergedAuditLogsAsync(startDate, endDate))
            .Select(l => l.Action)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a)
            .ToList();
    }

    private async Task<List<AuditLog>> GetMergedAuditLogsAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        var localTask = GetLocalAuditLogsAsync(startDate, endDate);
        var apiTask = GetApiAuditLogsAsync(startDate, endDate);

        await Task.WhenAll(localTask, apiTask);

        return DeduplicateLogs(localTask.Result.Concat(apiTask.Result));
    }

    private async Task<List<AuditLog>> GetLocalAuditLogsAsync(DateTime? startDate, DateTime? endDate)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (startDate.HasValue)
            query = query.Where(l => l.Timestamp >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(l => l.Timestamp <= endDate.Value);

        return await query
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id)
            .ToListAsync();
    }

    private async Task<List<AuditLog>> GetApiAuditLogsAsync(DateTime? startDate, DateTime? endDate)
    {
        var logs = new List<AuditLog>();
        var page = 1;

        try
        {
            while (true)
            {
                var response = await _httpClient.GetFromJsonAsync<ApiPagedResult<ApiAuditLogItem>>(
                    BuildApiActivityUrl(page, ApiActivityPageSize, startDate, endDate));

                if (response?.Items == null || response.Items.Count == 0)
                    break;

                logs.AddRange(response.Items.Select(MapToAuditLog));

                if (response.Items.Count < ApiActivityPageSize || page >= response.TotalPages)
                    break;

                page++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load API audit logs; falling back to legacy web audit logs only");
        }

        return logs;
    }

    private async Task<List<string>> GetLocalDistinctActionsAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.AuditLogs
            .AsNoTracking()
            .Where(log => log.Action != null && log.Action != string.Empty)
            .Select(log => log.Action)
            .Distinct()
            .OrderBy(action => action)
            .ToListAsync();
    }

    private async Task<List<string>> GetLocalDistinctUsersAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        return await db.AuditLogs
            .AsNoTracking()
            .Where(log => log.Username != null && log.Username != string.Empty)
            .Select(log => log.Username)
            .Distinct()
            .OrderBy(username => username)
            .ToListAsync();
    }

    private async Task<ApiAuditFilterOptions> GetApiAuditFilterOptionsAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<ApiAuditFilterOptions>(
                BuildApiFilterOptionsUrl(startDate, endDate));

            return response ?? new ApiAuditFilterOptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load API audit filter options; falling back to legacy web audit logs only");
            return new ApiAuditFilterOptions();
        }
    }

    private static IQueryable<AuditLog> BuildLocalAuditQuery(
        WebAppDbContext db,
        DateTime? startDate,
        DateTime? endDate,
        string? username,
        string? action)
    {
        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (startDate.HasValue)
            query = query.Where(log => log.Timestamp >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(log => log.Timestamp <= endDate.Value);

        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(log => log.Username == username);

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(log => log.Action == action);

        return query;
    }

    private async Task<(List<AuditLog> Items, bool HasMore, int TotalCount)> QueryMergedAuditLogsAsync(
        DateTime? startDate,
        DateTime? endDate,
        string? username,
        string? action,
        int skipCount,
        int takeCount,
        bool includeTotalCount)
    {
        var pageItems = takeCount > 0 ? new List<AuditLog>(takeCount) : new List<AuditLog>();
        var latestAcceptedTimestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var acceptedCount = 0;

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var localBuffer = new List<AuditLog>();
        var localIndex = 0;
        var localOffset = 0;
        var localExhausted = false;
        AuditLog? localCurrent = null;

        var apiBuffer = new List<AuditLog>();
        var apiIndex = 0;
        var apiPage = 1;
        var apiExhausted = false;
        AuditLog? apiCurrent = null;

        async Task<bool> MoveLocalAsync()
        {
            while (true)
            {
                if (localIndex < localBuffer.Count)
                {
                    localCurrent = localBuffer[localIndex++];
                    return true;
                }

                if (localExhausted)
                {
                    localCurrent = null;
                    return false;
                }

                localBuffer = await BuildLocalAuditQuery(db, startDate, endDate, username, action)
                    .OrderByDescending(log => log.Timestamp)
                    .ThenByDescending(log => log.Id)
                    .Skip(localOffset)
                    .Take(LocalAuditPageSize)
                    .ToListAsync();

                localOffset += localBuffer.Count;
                localIndex = 0;

                if (localBuffer.Count == 0)
                {
                    localExhausted = true;
                }
            }
        }

        async Task<bool> MoveApiAsync()
        {
            while (true)
            {
                if (apiIndex < apiBuffer.Count)
                {
                    apiCurrent = apiBuffer[apiIndex++];
                    return true;
                }

                if (apiExhausted)
                {
                    apiCurrent = null;
                    return false;
                }

                try
                {
                    var response = await _httpClient.GetFromJsonAsync<ApiPagedResult<ApiAuditLogItem>>(
                        BuildApiActivityUrl(apiPage, ApiActivityPageSize, startDate, endDate, username, action));

                    if (response?.Items == null || response.Items.Count == 0)
                    {
                        apiExhausted = true;
                        continue;
                    }

                    apiBuffer = response.Items
                        .Select(MapToAuditLog)
                        .ToList();
                    apiIndex = 0;
                    apiPage++;

                    if (response.Items.Count < ApiActivityPageSize || response.Page >= response.TotalPages)
                    {
                        apiExhausted = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to stream API audit logs; falling back to legacy web audit logs only");
                    apiExhausted = true;
                }
            }
        }

        var hasLocal = await MoveLocalAsync();
        var hasApi = await MoveApiAsync();

        while (hasLocal || hasApi)
        {
            var takeLocal = ShouldTakeLocal(localCurrent, apiCurrent);
            var current = takeLocal ? localCurrent! : apiCurrent!;

            if (TryKeepDeduplicatedLog(current, latestAcceptedTimestamps))
            {
                acceptedCount++;

                if (takeCount > 0 && acceptedCount > skipCount && pageItems.Count < takeCount)
                {
                    pageItems.Add(current);
                }

                if (!includeTotalCount && takeCount > 0 && acceptedCount > skipCount + takeCount)
                {
                    return (pageItems, true, 0);
                }
            }

            if (takeLocal)
            {
                hasLocal = await MoveLocalAsync();
            }
            else
            {
                hasApi = await MoveApiAsync();
            }
        }

        var hasMore = takeCount > 0 && acceptedCount > skipCount + takeCount;
        return (pageItems, hasMore, acceptedCount);
    }

    private static (DateTime? StartDateUtc, DateTime? EndDateUtc) NormalizeAuditDateRange(DateTime? fromDate, DateTime? toDate)
    {
        DateTime? startDateUtc = fromDate.HasValue ? IAuditService.ToUTC(fromDate.Value.Date) : null;
        DateTime? endDateUtc = toDate.HasValue ? IAuditService.ToUTC(toDate.Value.Date.AddDays(1)).AddTicks(-1) : null;
        return (startDateUtc, endDateUtc);
    }

    private static bool ShouldTakeLocal(AuditLog? localLog, AuditLog? apiLog)
    {
        if (localLog is null)
        {
            return false;
        }

        if (apiLog is null)
        {
            return true;
        }

        var timestampCompare = localLog.Timestamp.CompareTo(apiLog.Timestamp);
        if (timestampCompare != 0)
        {
            return timestampCompare > 0;
        }

        var idCompare = localLog.Id.CompareTo(apiLog.Id);
        return idCompare >= 0;
    }

    private static string BuildApiActivityUrl(
        int page,
        int pageSize,
        DateTime? startDate,
        DateTime? endDate,
        string? username = null,
        string? action = null)
    {
        var query = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}"
        };

        if (startDate.HasValue)
            query.Add($"startDate={Uri.EscapeDataString(startDate.Value.ToString("O"))}");

        if (endDate.HasValue)
            query.Add($"endDate={Uri.EscapeDataString(endDate.Value.ToString("O"))}");

        if (!string.IsNullOrWhiteSpace(username))
            query.Add($"username={Uri.EscapeDataString(username)}");

        if (!string.IsNullOrWhiteSpace(action))
            query.Add($"action={Uri.EscapeDataString(action)}");

        return $"api/useractivity?{string.Join("&", query)}";
    }

    private static string BuildApiFilterOptionsUrl(DateTime? startDate, DateTime? endDate)
    {
        var query = new List<string>();

        if (startDate.HasValue)
            query.Add($"startDate={Uri.EscapeDataString(startDate.Value.ToString("O"))}");

        if (endDate.HasValue)
            query.Add($"endDate={Uri.EscapeDataString(endDate.Value.ToString("O"))}");

        return query.Count == 0
            ? "api/useractivity/filter-options"
            : $"api/useractivity/filter-options?{string.Join("&", query)}";
    }

    private static AuditLog MapToAuditLog(ApiAuditLogItem item) => new()
    {
        Id = item.Id,
        Username = item.Username ?? string.Empty,
        UserRole = item.UserRole ?? string.Empty,
        Action = item.Action ?? string.Empty,
        EntityType = item.EntityType,
        EntityId = item.EntityId,
        Details = item.Details,
        IpAddress = item.IpAddress,
        UserAgent = item.UserAgent,
        PageUrl = item.PageUrl,
        IsSuccess = item.IsSuccess,
        ErrorMessage = item.ErrorMessage,
        Timestamp = item.Timestamp
    };

    private static ActivityLog MapToActivityLog(AuditLog log) => new()
    {
        Username = log.Username,
        Action = log.Action,
        EntityType = log.EntityType,
        EntityId = log.EntityId,
        Details = log.Details,
        IsSuccess = log.IsSuccess,
        Timestamp = log.Timestamp
    };

    private static List<AuditLog> DeduplicateLogs(IEnumerable<AuditLog> logs)
    {
        var deduped = new List<AuditLog>();

        var latestAcceptedTimestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var log in logs
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id))
        {
            if (!TryKeepDeduplicatedLog(log, latestAcceptedTimestamps))
            {
                continue;
            }

            deduped.Add(log);
        }

        return deduped
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id)
            .ToList();
    }

    private static bool TryKeepDeduplicatedLog(AuditLog log, IDictionary<string, DateTime> latestAcceptedTimestamps)
    {
        var dedupKey = BuildDedupKey(log);
        if (latestAcceptedTimestamps.TryGetValue(dedupKey, out var latestAcceptedTimestamp) &&
            latestAcceptedTimestamp - log.Timestamp <= AuditDeduplicationWindow)
        {
            return false;
        }

        latestAcceptedTimestamps[dedupKey] = log.Timestamp;
        return true;
    }

    private static string BuildDedupKey(AuditLog log)
    {
        return string.Join('\u001F',
            (log.Username ?? string.Empty).ToUpperInvariant(),
            (log.UserRole ?? string.Empty).ToUpperInvariant(),
            log.Action ?? string.Empty,
            log.EntityType ?? string.Empty,
            log.EntityId ?? string.Empty,
            log.Details ?? string.Empty,
            log.ErrorMessage ?? string.Empty,
            log.PageUrl ?? string.Empty,
            log.IsSuccess);
    }
}
