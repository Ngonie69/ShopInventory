using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Service implementation for API Rate Limiting
/// </summary>
public class RateLimitService : IRateLimitService
{
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RateLimitService> _logger;

    private int _defaultMaxRequests;
    private int _defaultWindowSeconds;
    private int _blockDurationMinutes;

    public RateLimitService(
        ApplicationDbContext context,
        IConfiguration configuration,
        ILogger<RateLimitService> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;

        _defaultMaxRequests = configuration.GetValue<int>("RateLimit:MaxRequests", 100);
        _defaultWindowSeconds = configuration.GetValue<int>("RateLimit:WindowSeconds", 60);
        _blockDurationMinutes = configuration.GetValue<int>("RateLimit:BlockDurationMinutes", 15);
    }

    public async Task<RateLimitDashboardDto> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var dayAgo = now.AddHours(-24);

        var totalClients = await _context.ApiRateLimits
            .CountAsync(cancellationToken);

        var blockedClients = await _context.ApiRateLimits
            .Where(r => r.IsBlocked && (r.BlockExpiresAt == null || r.BlockExpiresAt > now))
            .CountAsync(cancellationToken);

        var recentRequests = await _context.ApiRateLimits
            .Where(r => r.LastRequestAt >= dayAgo)
            .SumAsync(r => r.RequestCount, cancellationToken);

        var totalBlocks = await _context.ApiRateLimits
            .SumAsync(r => r.TotalBlockedCount, cancellationToken);

        var topClients = await _context.ApiRateLimits
            .OrderByDescending(r => r.RequestCount)
            .Take(10)
            .ToListAsync(cancellationToken);

        var blockedList = await _context.ApiRateLimits
            .Where(r => r.IsBlocked)
            .OrderByDescending(r => r.TotalBlockedCount)
            .Take(20)
            .ToListAsync(cancellationToken);

        return new RateLimitDashboardDto
        {
            TotalClients = totalClients,
            BlockedClients = blockedClients,
            TotalRequests24h = recentRequests,
            TotalBlocks24h = totalBlocks,
            TopClients = topClients.Select(MapToDto).ToList(),
            BlockedClientsList = blockedList.Select(MapToDto).ToList()
        };
    }

    public async Task<ApiRateLimitDto?> GetClientLimitAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var limit = await _context.ApiRateLimits
            .FirstOrDefaultAsync(r => r.ClientId == clientId, cancellationToken);

        return limit == null ? null : MapToDto(limit);
    }

    public async Task<bool> CheckRateLimitAsync(string clientId, string clientType, string? endpoint = null, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var limit = await _context.ApiRateLimits
            .FirstOrDefaultAsync(r => r.ClientId == clientId, cancellationToken);

        if (limit == null)
        {
            // New client, create record
            limit = new ApiRateLimitEntity
            {
                ClientId = clientId,
                ClientType = clientType,
                Endpoint = endpoint,
                RequestCount = 0,
                WindowStart = now,
                WindowDurationSeconds = _defaultWindowSeconds,
                MaxRequests = _defaultMaxRequests
            };
            _context.ApiRateLimits.Add(limit);
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }

        // Check if currently blocked
        if (limit.IsBlocked)
        {
            if (limit.BlockExpiresAt.HasValue && limit.BlockExpiresAt <= now)
            {
                // Block expired, reset
                limit.IsBlocked = false;
                limit.RequestCount = 0;
                limit.WindowStart = now;
            }
            else
            {
                return false; // Still blocked
            }
        }

        // Check if window expired
        var windowEnd = limit.WindowStart.AddSeconds(limit.WindowDurationSeconds);
        if (now >= windowEnd)
        {
            // Reset window
            limit.RequestCount = 0;
            limit.WindowStart = now;
        }

        // Check if over limit
        if (limit.RequestCount >= limit.MaxRequests)
        {
            limit.IsBlocked = true;
            limit.BlockExpiresAt = now.AddMinutes(_blockDurationMinutes);
            limit.TotalBlockedCount++;
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogWarning("Rate limit exceeded for client {ClientId}, blocked until {BlockExpiry}",
                clientId, limit.BlockExpiresAt);

            return false;
        }

        return true;
    }

    public async Task IncrementRequestCountAsync(string clientId, string clientType, string? endpoint = null, CancellationToken cancellationToken = default)
    {
        var limit = await _context.ApiRateLimits
            .FirstOrDefaultAsync(r => r.ClientId == clientId, cancellationToken);

        if (limit != null)
        {
            limit.RequestCount++;
            limit.LastRequestAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> UnblockClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var limit = await _context.ApiRateLimits
            .FirstOrDefaultAsync(r => r.ClientId == clientId, cancellationToken);

        if (limit == null)
            return false;

        limit.IsBlocked = false;
        limit.BlockExpiresAt = null;
        limit.RequestCount = 0;
        limit.WindowStart = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Unblocked client {ClientId}", clientId);
        return true;
    }

    public async Task UpdateSettingsAsync(UpdateRateLimitSettingsRequest settings, CancellationToken cancellationToken = default)
    {
        _defaultMaxRequests = settings.DefaultMaxRequests;
        _defaultWindowSeconds = settings.DefaultWindowSeconds;
        _blockDurationMinutes = settings.BlockDurationMinutes;

        // Update all existing records with new defaults
        var allLimits = await _context.ApiRateLimits.ToListAsync(cancellationToken);
        foreach (var limit in allLimits)
        {
            limit.MaxRequests = settings.DefaultMaxRequests;
            limit.WindowDurationSeconds = settings.DefaultWindowSeconds;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task CleanupOldRecordsAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var oldRecords = await _context.ApiRateLimits
            .Where(r => r.LastRequestAt < cutoff && !r.IsBlocked)
            .ToListAsync(cancellationToken);

        _context.ApiRateLimits.RemoveRange(oldRecords);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Cleaned up {Count} old rate limit records", oldRecords.Count);
    }

    public async Task<RateLimitListResponseDto> GetAllAsync(int page, int pageSize, bool? blockedOnly = null, CancellationToken cancellationToken = default)
    {
        var query = _context.ApiRateLimits.AsQueryable();

        if (blockedOnly == true)
        {
            var now = DateTime.UtcNow;
            query = query.Where(r => r.IsBlocked && (r.BlockExpiresAt == null || r.BlockExpiresAt > now));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.LastRequestAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new RateLimitListResponseDto
        {
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Items = items.Select(MapToDto).ToList()
        };
    }

    public async Task<ApiRateLimitDto?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var limit = await _context.ApiRateLimits
            .FirstOrDefaultAsync(r => r.ClientId == clientId, cancellationToken);
        return limit == null ? null : MapToDto(limit);
    }

    public async Task<RateLimitStatusDto> GetRateLimitStatusAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var limit = await _context.ApiRateLimits
            .FirstOrDefaultAsync(r => r.ClientId == clientId, cancellationToken);

        if (limit == null)
        {
            return new RateLimitStatusDto
            {
                ClientId = clientId,
                RequestsInWindow = 0,
                MaxRequests = _defaultMaxRequests,
                WindowSizeSeconds = _defaultWindowSeconds,
                WindowResetAt = now.AddSeconds(_defaultWindowSeconds),
                IsBlocked = false,
                BlockedUntil = null
            };
        }

        var windowResetAt = limit.WindowStart.AddSeconds(limit.WindowDurationSeconds);
        if (windowResetAt < now)
        {
            windowResetAt = now.AddSeconds(limit.WindowDurationSeconds);
        }

        return new RateLimitStatusDto
        {
            ClientId = clientId,
            RequestsInWindow = limit.RequestCount,
            MaxRequests = limit.MaxRequests,
            WindowSizeSeconds = limit.WindowDurationSeconds,
            WindowResetAt = windowResetAt,
            IsBlocked = limit.IsBlocked && (limit.BlockExpiresAt == null || limit.BlockExpiresAt > now),
            BlockedUntil = limit.BlockExpiresAt
        };
    }

    public async Task<bool> IsRequestAllowedAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var status = await GetRateLimitStatusAsync(clientId, cancellationToken);
        return !status.IsBlocked && status.RemainingRequests > 0;
    }

    public async Task BlockClientAsync(string clientId, int durationMinutes, string? reason = null, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var limit = await _context.ApiRateLimits
            .FirstOrDefaultAsync(r => r.ClientId == clientId, cancellationToken);

        if (limit == null)
        {
            limit = new ApiRateLimitEntity
            {
                ClientId = clientId,
                ClientType = "Manual",
                WindowStart = now,
                WindowDurationSeconds = _defaultWindowSeconds,
                MaxRequests = _defaultMaxRequests,
                LastRequestAt = now
            };
            _context.ApiRateLimits.Add(limit);
        }

        limit.IsBlocked = true;
        limit.BlockExpiresAt = now.AddMinutes(durationMinutes);
        limit.TotalBlockedCount++;

        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Blocked client {ClientId} for {Duration} minutes. Reason: {Reason}", clientId, durationMinutes, reason ?? "Manual block");
    }

    public async Task<bool> ResetClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var limit = await _context.ApiRateLimits
            .FirstOrDefaultAsync(r => r.ClientId == clientId, cancellationToken);

        if (limit == null)
            return false;

        limit.RequestCount = 0;
        limit.WindowStart = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<List<ApiRateLimitDto>> GetBlockedClientsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var blocked = await _context.ApiRateLimits
            .Where(r => r.IsBlocked && (r.BlockExpiresAt == null || r.BlockExpiresAt > now))
            .OrderByDescending(r => r.TotalBlockedCount)
            .ToListAsync(cancellationToken);

        return blocked.Select(MapToDto).ToList();
    }

    public async Task<RateLimitStatsDto> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var today = now.Date;

        var stats = new RateLimitStatsDto
        {
            TotalClients = await _context.ApiRateLimits.CountAsync(cancellationToken),
            ActiveClients = await _context.ApiRateLimits
                .CountAsync(r => r.LastRequestAt >= today, cancellationToken),
            BlockedClients = await _context.ApiRateLimits
                .CountAsync(r => r.IsBlocked && (r.BlockExpiresAt == null || r.BlockExpiresAt > now), cancellationToken),
            TotalRequestsToday = await _context.ApiRateLimits
                .Where(r => r.LastRequestAt >= today)
                .SumAsync(r => r.RequestCount, cancellationToken),
            TotalBlocksToday = await _context.ApiRateLimits
                .CountAsync(r => r.TotalBlockedCount > 0, cancellationToken)
        };

        stats.AverageRequestsPerClient = stats.TotalClients > 0
            ? (double)stats.TotalRequestsToday / stats.TotalClients
            : 0;

        return stats;
    }

    public RateLimitConfigDto GetConfiguration()
    {
        return new RateLimitConfigDto
        {
            MaxRequests = _defaultMaxRequests,
            WindowSizeSeconds = _defaultWindowSeconds,
            BlockDurationMinutes = _blockDurationMinutes,
            IsEnabled = true,
            WhitelistedIPs = new List<string>(),
            WhitelistedApiKeys = new List<string>()
        };
    }

    public async Task UpdateConfigurationAsync(RateLimitConfigDto config, CancellationToken cancellationToken = default)
    {
        _defaultMaxRequests = config.MaxRequests;
        _defaultWindowSeconds = config.WindowSizeSeconds;
        _blockDurationMinutes = config.BlockDurationMinutes;

        await Task.CompletedTask;
        _logger.LogInformation("Rate limit configuration updated: MaxRequests={MaxRequests}, WindowSize={WindowSize}s, BlockDuration={BlockDuration}min",
            config.MaxRequests, config.WindowSizeSeconds, config.BlockDurationMinutes);
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Unblock expired blocks
        var expiredBlocks = await _context.ApiRateLimits
            .Where(r => r.IsBlocked && r.BlockExpiresAt != null && r.BlockExpiresAt <= now)
            .ToListAsync(cancellationToken);

        foreach (var block in expiredBlocks)
        {
            block.IsBlocked = false;
        }

        // Remove old records
        var cutoff = now.AddDays(-7);
        var oldRecords = await _context.ApiRateLimits
            .Where(r => r.LastRequestAt < cutoff && !r.IsBlocked)
            .ToListAsync(cancellationToken);

        _context.ApiRateLimits.RemoveRange(oldRecords);
        await _context.SaveChangesAsync(cancellationToken);

        var totalCleaned = expiredBlocks.Count + oldRecords.Count;
        _logger.LogInformation("Cleaned up {UnblockedCount} expired blocks and {RemovedCount} old records", expiredBlocks.Count, oldRecords.Count);

        return totalCleaned;
    }

    private static ApiRateLimitDto MapToDto(ApiRateLimitEntity entity)
    {
        return new ApiRateLimitDto
        {
            Id = entity.Id,
            ClientId = entity.ClientId,
            ClientType = entity.ClientType,
            Endpoint = entity.Endpoint,
            RequestCount = entity.RequestCount,
            WindowStart = entity.WindowStart,
            WindowDurationSeconds = entity.WindowDurationSeconds,
            MaxRequests = entity.MaxRequests,
            IsBlocked = entity.IsBlocked,
            BlockExpiresAt = entity.BlockExpiresAt,
            TotalBlockedCount = entity.TotalBlockedCount,
            LastRequestAt = entity.LastRequestAt
        };
    }
}
