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
    private static readonly Lazy<TimeZoneInfo> CatTimeZone = new(ResolveCatTimeZone);

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

    public static DateTime ToCAT(DateTime utcDateTime)
    {
        var normalizedUtc = utcDateTime.Kind switch
        {
            DateTimeKind.Utc => utcDateTime,
            DateTimeKind.Local => utcDateTime.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc)
        };

        return TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, CatTimeZone.Value);
    }

    public static DateTime FromCAT(DateTime catDateTime)
    {
        var normalizedCat = DateTime.SpecifyKind(catDateTime, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(normalizedCat, CatTimeZone.Value);
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
                UserId = ResolveCurrentUserId(username),
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
                AppVersion = httpContext?.Request.Headers["X-App-Version"].FirstOrDefault(),
                DeviceModel = httpContext?.Request.Headers["X-Device-Model"].FirstOrDefault(),
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

        var preferredIdentity = ResolvePreferredIdentity(user);
        if (preferredIdentity != null)
        {
            var username = preferredIdentity.Name
                ?? preferredIdentity.FindFirst(ClaimTypes.Name)?.Value
                ?? "Unknown";
            var role = preferredIdentity.FindFirst(ClaimTypes.Role)?.Value ?? "User";
            return (username, role);
        }

        return ("Anonymous", "None");
    }

    private string? ResolveCurrentUserId(string username)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var user = httpContext?.User;

        var preferredIdentity = ResolvePreferredIdentity(user);
        if (preferredIdentity == null)
        {
            return null;
        }

        var currentUsername = preferredIdentity.Name ?? preferredIdentity.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrWhiteSpace(currentUsername) ||
            !string.Equals(currentUsername, username, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return preferredIdentity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    private static ClaimsIdentity? ResolvePreferredIdentity(ClaimsPrincipal? user)
    {
        if (user == null)
        {
            return null;
        }

        var identities = user.Identities
            .Where(identity => identity.IsAuthenticated)
            .OfType<ClaimsIdentity>()
            .ToList();

        if (identities.Count == 0)
        {
            return null;
        }

        return identities.FirstOrDefault(identity => !IsApiKeyIdentity(identity) && HasDisplayableUser(identity))
            ?? identities.FirstOrDefault(HasDisplayableUser)
            ?? identities.First();
    }

    private static bool IsApiKeyIdentity(ClaimsIdentity identity)
    {
        return string.Equals(identity.FindFirst(ClaimTypes.AuthenticationMethod)?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase)
            || identity.HasClaim(claim => string.Equals(claim.Type, "api_key_name", StringComparison.Ordinal));
    }

    private static bool HasDisplayableUser(ClaimsIdentity identity)
    {
        var username = identity.Name ?? identity.FindFirst(ClaimTypes.Name)?.Value;
        return !string.IsNullOrWhiteSpace(username);
    }

    private static string? GetClientIpAddress(HttpContext? httpContext)
    {
        if (httpContext == null) return null;
        return httpContext.Connection.RemoteIpAddress?.ToString();
    }

    private static TimeZoneInfo ResolveCatTimeZone()
    {
        foreach (var timeZoneId in new[] { "South Africa Standard Time", "Africa/Harare", "Africa/Blantyre", "Africa/Lusaka" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
