namespace ShopInventory.Web.Middleware;

/// <summary>
/// Security headers middleware for the Blazor Server web application.
/// Protects against: Clickjacking, XSS, MIME sniffing, content injection, data leakage.
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Register callback to add headers just before the response is sent
        context.Response.OnStarting(() =>
        {
            AddSecurityHeaders(context);
            return Task.CompletedTask;
        });

        await _next(context);
    }

    private static void AddSecurityHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Prevent clickjacking
        headers["X-Frame-Options"] = "SAMEORIGIN"; // SAMEORIGIN for Blazor Server (uses iframes for error UI)

        // Prevent MIME type sniffing
        headers["X-Content-Type-Options"] = "nosniff";

        // XSS filter in browsers
        headers["X-XSS-Protection"] = "1; mode=block";

        // Control referrer information
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Content Security Policy for Blazor Server
        // Blazor Server requires 'unsafe-inline' for styles (MudBlazor), 'unsafe-eval' for JS interop
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +  // Blazor SignalR + import maps + JS interop
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net; " +
            "img-src 'self' data: https:; " +
            "font-src 'self' data: https://fonts.gstatic.com https://cdn.jsdelivr.net; " +
            "connect-src 'self' ws: wss: https://fonts.googleapis.com https://fonts.gstatic.com; " + // Blazor SignalR + Google Fonts
            "frame-src 'self' https://www.openstreetmap.org; " +  // Blazor Server error UI + map embeds
            "frame-ancestors 'self'; " +
            "base-uri 'self'; " +
            "form-action 'self'; " +
            "object-src 'none';";

        // Permissions Policy - restrict browser features
        headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=(), interest-cohort=()";

        // Remove server identification
        headers.Remove("Server");
        headers.Remove("X-Powered-By");
    }
}

/// <summary>
/// Middleware to validate request inputs for the web application.
/// Protects against: Path traversal, injection attacks via query strings.
/// </summary>
public class RequestValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestValidationMiddleware> _logger;

    // Suspicious patterns that should never appear in web app URLs
    private static readonly string[] SuspiciousPatterns =
    {
        "../", "..\\", "%2e%2e", "%252e%252e",
        "<script", "javascript:", "vbscript:",
        "onload=", "onerror=", "onclick=",
        "union select", "drop table", "insert into",
        "exec(", "execute(", "xp_cmdshell",
    };

    private static readonly string[] BotUserAgentMarkers =
    {
        "bingbot", "googlebot", "googleother", "adsbot", "slurp",
        "duckduckbot", "baiduspider", "yandexbot", "applebot",
        "petalbot", "facebookexternalhit", "crawler", "spider"
    };

    public RequestValidationMiddleware(RequestDelegate next, ILogger<RequestValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.ToString();
        var query = context.Request.QueryString.ToString();
        var userAgent = context.Request.Headers.UserAgent.ToString();

        // Skip validation for Blazor internal paths (SignalR, static files)
        if (path.StartsWith("/_blazor") || path.StartsWith("/_framework") || path.StartsWith("/_content"))
        {
            await _next(context);
            return;
        }

        if (IsSensitiveAuthPath(path) && IsBlockedBotUserAgent(userAgent))
        {
            _logger.LogWarning("Blocked crawler request from {Ip} to {Path} with user agent {UserAgent}",
                context.Connection.RemoteIpAddress, path, userAgent);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            return;
        }

        if (ContainsSuspicious(path) || ContainsSuspicious(query))
        {
            _logger.LogWarning("Blocked suspicious request from {Ip}: {Path}{Query}",
                context.Connection.RemoteIpAddress, path, query);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await _next(context);
    }

    private static bool ContainsSuspicious(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(content).ToLowerInvariant();
        }
        catch
        {
            return true; // Malformed encoding
        }

        return SuspiciousPatterns.Any(p => decoded.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSensitiveAuthPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return path.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/customer-login", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/forgot-password", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/register", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockedBotUserAgent(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return false;

        return BotUserAgentMarkers.Any(marker => userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Middleware to add rate limiting for the Blazor Server application.
/// Uses a simple sliding window approach per IP address.
/// Protects against: DDoS, brute force on login pages.
/// </summary>
public class SimpleRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SimpleRateLimitMiddleware> _logger;

    // Track requests per IP: IP -> list of request timestamps
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Queue<DateTime>> RequestLog = new();

    // General: 200 requests per minute per IP
    private const int MaxRequestsPerMinute = 200;
    // Auth pages: 20 requests per minute per IP
    private const int MaxAuthRequestsPerMinute = 20;
    // Cleanup interval
    private static DateTime _lastCleanup = DateTime.UtcNow;
    private static readonly object CleanupLock = new();

    public SimpleRateLimitMiddleware(RequestDelegate next, ILogger<SimpleRateLimitMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // Skip rate limiting for static files
        if (path.StartsWith("/_content") || path.StartsWith("/_framework") ||
            path.StartsWith("/css") || path.StartsWith("/js") || path.StartsWith("/images") ||
            path.EndsWith(".css") || path.EndsWith(".js") || path.EndsWith(".woff2"))
        {
            await _next(context);
            return;
        }

        // Periodic cleanup of old entries
        CleanupOldEntries();

        var isAuthPath = path.Contains("/login") || path.Contains("/customer-login") ||
                         path.Contains("/forgot-password") || path.Contains("/register");
        var maxRequests = isAuthPath ? MaxAuthRequestsPerMinute : MaxRequestsPerMinute;
        var key = isAuthPath ? $"auth:{ip}" : ip;

        var queue = RequestLog.GetOrAdd(key, _ => new Queue<DateTime>());
        var now = DateTime.UtcNow;
        var windowStart = now.AddMinutes(-1);

        lock (queue)
        {
            // Remove old entries outside the window
            while (queue.Count > 0 && queue.Peek() < windowStart)
                queue.Dequeue();

            if (queue.Count >= maxRequests)
            {
                _logger.LogWarning("Rate limit exceeded for IP {Ip} on path {Path} ({Count} requests/min)",
                    ip, path, queue.Count);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers["Retry-After"] = "60";
                return;
            }

            queue.Enqueue(now);
        }

        await _next(context);
    }

    private static void CleanupOldEntries()
    {
        if ((DateTime.UtcNow - _lastCleanup).TotalMinutes < 5)
            return;

        lock (CleanupLock)
        {
            if ((DateTime.UtcNow - _lastCleanup).TotalMinutes < 5)
                return;

            var cutoff = DateTime.UtcNow.AddMinutes(-2);
            var keysToRemove = RequestLog
                .Where(kvp => { lock (kvp.Value) { return kvp.Value.Count == 0 || kvp.Value.Peek() < cutoff; } })
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
                RequestLog.TryRemove(key, out _);

            _lastCleanup = DateTime.UtcNow;
        }
    }
}

/// <summary>
/// Extension methods for Web security middleware
/// </summary>
public static class WebSecurityMiddlewareExtensions
{
    public static IApplicationBuilder UseWebSecurityHeaders(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SecurityHeadersMiddleware>();
    }

    public static IApplicationBuilder UseWebRequestValidation(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RequestValidationMiddleware>();
    }

    public static IApplicationBuilder UseSimpleRateLimit(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SimpleRateLimitMiddleware>();
    }
}
