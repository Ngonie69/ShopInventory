using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

public interface IAuditService
{
    Task LogAsync(string action, string username, string userRole, string? entityType = null,
        string? entityId = null, string? details = null, string? endpoint = null,
        bool isSuccess = true, string? errorMessage = null);

    Task LogAsync(string action, string? entityType = null, string? entityId = null);

    Task LogAsync(string action, string? entityType, string? entityId, string? details,
        bool isSuccess, string? errorMessage = null);
}

public class AuditService : IAuditService
{
    private readonly ApplicationDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditService> _logger;

    public AuditService(
        ApplicationDbContext db,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuditService> logger)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task LogAsync(string action, string username, string userRole, string? entityType = null,
        string? entityId = null, string? details = null, string? endpoint = null,
        bool isSuccess = true, string? errorMessage = null)
    {
        try
        {
            var httpContext = _httpContextAccessor.HttpContext;

            var log = new AuditLog
            {
                Action = action,
                Username = username,
                UserRole = userRole,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                IpAddress = GetClientIpAddress(httpContext),
                UserAgent = httpContext?.Request.Headers.UserAgent.ToString(),
                PageUrl = endpoint ?? $"{httpContext?.Request.Method} {httpContext?.Request.Path}",
                IsSuccess = isSuccess,
                ErrorMessage = errorMessage,
                Timestamp = DateTime.UtcNow
            };

            _db.AuditLogs.Add(log);
            await _db.SaveChangesAsync();

            _logger.LogDebug("Audit log created: {Action} by {Username}", action, username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create audit log for action {Action}", action);
        }
    }

    public async Task LogAsync(string action, string? entityType = null, string? entityId = null)
    {
        var (username, role) = ResolveCurrentUser();
        await LogAsync(action, username, role, entityType, entityId);
    }

    public async Task LogAsync(string action, string? entityType, string? entityId, string? details,
        bool isSuccess, string? errorMessage = null)
    {
        var (username, role) = ResolveCurrentUser();
        await LogAsync(action, username, role, entityType, entityId, details, null, isSuccess, errorMessage);
    }

    private (string username, string role) ResolveCurrentUser()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var user = httpContext?.User;

        if (user?.Identity?.IsAuthenticated == true)
        {
            var username = user.Identity.Name ?? user.FindFirst(ClaimTypes.Name)?.Value ?? "Unknown";
            var role = user.FindFirst(ClaimTypes.Role)?.Value ?? "User";
            return (username, role);
        }

        return ("Anonymous", "None");
    }

    private static string? GetClientIpAddress(HttpContext? httpContext)
    {
        if (httpContext == null) return null;

        // Check X-Forwarded-For first (behind reverse proxy/load balancer)
        var forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // Take the first IP (original client)
            return forwardedFor.Split(',', StringSplitOptions.TrimEntries)[0];
        }

        return httpContext.Connection.RemoteIpAddress?.ToString();
    }
}
