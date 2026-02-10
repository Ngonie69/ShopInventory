namespace ShopInventory.Middleware;

/// <summary>
/// Middleware to add security headers to all responses
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SecurityHeadersMiddleware> _logger;

    public SecurityHeadersMiddleware(RequestDelegate next, ILogger<SecurityHeadersMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Add security headers before processing the request
        AddSecurityHeaders(context);

        await _next(context);
    }

    private static void AddSecurityHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // Allow Swagger UI to be embedded in iframes (for web app integration)
        var isSwaggerPath = path.StartsWith("/swagger");

        // Prevent clickjacking attacks (except for Swagger which needs iframe embedding)
        if (!isSwaggerPath)
        {
            headers["X-Frame-Options"] = "DENY";
        }

        // Prevent MIME type sniffing
        headers["X-Content-Type-Options"] = "nosniff";

        // Enable XSS filter in browsers
        headers["X-XSS-Protection"] = "1; mode=block";

        // Control referrer information
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Content Security Policy - adjust based on your needs
        // Allow Swagger to be embedded from any origin (for web app integration)
        var frameAncestors = isSwaggerPath ? "frame-ancestors *;" : "frame-ancestors 'none';";
        headers["Content-Security-Policy"] = "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
            "style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data: https:; " +
            "font-src 'self'; " +
            frameAncestors;

        // Permissions Policy (formerly Feature-Policy)
        headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

        // Cache control for API responses
        if (!headers.ContainsKey("Cache-Control"))
        {
            headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        }

        // Prevent caching of sensitive data
        headers["Pragma"] = "no-cache";
        headers["Expires"] = "0";
    }
}

/// <summary>
/// Middleware to log and potentially block suspicious requests
/// </summary>
public class RequestValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestValidationMiddleware> _logger;
    private static readonly HashSet<string> SuspiciousPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "../",
        "..\\",
        "<script",
        "javascript:",
        "onclick",
        "onerror",
        "onload",
        "eval(",
        "exec(",
        "union select",
        "drop table",
        "insert into",
        "delete from",
        "update set",
        "' or '",
        "\" or \"",
        "1=1",
        "1 = 1"
    };

    public RequestValidationMiddleware(RequestDelegate next, ILogger<RequestValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Validate query string
        var queryString = context.Request.QueryString.ToString();
        if (ContainsSuspiciousContent(queryString))
        {
            _logger.LogWarning("Suspicious query string detected from IP {IpAddress}: {QueryString}",
                context.Connection.RemoteIpAddress, queryString);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { message = "Invalid request" });
            return;
        }

        // Validate path
        var path = context.Request.Path.ToString();
        if (ContainsSuspiciousContent(path))
        {
            _logger.LogWarning("Suspicious path detected from IP {IpAddress}: {Path}",
                context.Connection.RemoteIpAddress, path);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { message = "Invalid request" });
            return;
        }

        await _next(context);
    }

    private static bool ContainsSuspiciousContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        var decodedContent = Uri.UnescapeDataString(content);

        return SuspiciousPatterns.Any(pattern =>
            decodedContent.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Extension methods for security middleware
/// </summary>
public static class SecurityMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SecurityHeadersMiddleware>();
    }

    public static IApplicationBuilder UseRequestValidation(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RequestValidationMiddleware>();
    }
}
