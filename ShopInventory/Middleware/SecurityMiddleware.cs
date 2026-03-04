using System.Text;
using System.Text.RegularExpressions;

namespace ShopInventory.Middleware;

/// <summary>
/// Middleware to add security headers to all responses.
/// Protects against: Clickjacking, XSS, MIME sniffing, data leakage, content injection.
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SecurityHeadersMiddleware> _logger;
    private readonly bool _isDevelopment;

    public SecurityHeadersMiddleware(RequestDelegate next, ILogger<SecurityHeadersMiddleware> logger, IWebHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _isDevelopment = env.IsDevelopment();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Add security headers before processing the request
        AddSecurityHeaders(context);

        await _next(context);
    }

    private void AddSecurityHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        var isSwaggerPath = path.StartsWith("/swagger");

        // Prevent clickjacking - DENY for all API endpoints, Swagger only in development
        if (!isSwaggerPath || !_isDevelopment)
        {
            headers["X-Frame-Options"] = "DENY";
        }

        // Prevent MIME type sniffing (Content-Type Sniffing attacks)
        headers["X-Content-Type-Options"] = "nosniff";

        // XSS filter in browsers (legacy, but still useful for older browsers)
        headers["X-XSS-Protection"] = "1; mode=block";

        // Control referrer information leakage
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Strict Content Security Policy - NO unsafe-inline/unsafe-eval for API
        // Swagger needs relaxed CSP in development only
        if (isSwaggerPath && _isDevelopment)
        {
            headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data:; " +
                "font-src 'self'; " +
                "connect-src 'self'; " +
                "frame-ancestors 'self';";
        }
        else
        {
            headers["Content-Security-Policy"] =
                "default-src 'none'; " +
                "script-src 'self'; " +
                "style-src 'self'; " +
                "img-src 'self' data:; " +
                "font-src 'self'; " +
                "connect-src 'self'; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self';";
        }

        // Permissions Policy - restrict browser features
        headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=(), interest-cohort=()";

        // Prevent caching of API responses with sensitive data
        if (!headers.ContainsKey("Cache-Control"))
        {
            headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        }
        headers["Pragma"] = "no-cache";
        headers["Expires"] = "0";

        // Cross-Origin policies (relaxed for Swagger in development)
        if (isSwaggerPath && _isDevelopment)
        {
            headers["Cross-Origin-Opener-Policy"] = "unsafe-none";
            headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        }
        else
        {
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Cross-Origin-Embedder-Policy"] = "require-corp";
        }

        // Remove server identification headers
        headers.Remove("Server");
        headers.Remove("X-Powered-By");
    }
}

/// <summary>
/// Middleware to validate and block suspicious/malicious requests.
/// Protects against: SQL Injection, XSS, Path Traversal, Command Injection, XXE, Open Redirects.
/// Scans URL path, query string, headers, and request body.
/// </summary>
public class RequestValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestValidationMiddleware> _logger;

    // SQL Injection patterns
    private static readonly Regex SqlInjectionRegex = new(
        @"(\b(union\s+(all\s+)?select|insert\s+into|delete\s+from|update\s+.*set|drop\s+(table|database|index)|alter\s+table|create\s+(table|database)|exec(\s|\()|execute(\s|\()|xp_|sp_|0x[0-9a-f]+)\b)|('(\s|%20)*(or|and)(\s|%20)*')|(-{2})|(/\*.*\*/)|(\b(or|and)\b\s+\d+\s*=\s*\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    // XSS patterns
    private static readonly Regex XssRegex = new(
        @"(<\s*script[\s>]|javascript\s*:|on(click|error|load|mouseover|focus|blur|submit|change|keyup|keydown|input)\s*=|<\s*iframe[\s>]|<\s*object[\s>]|<\s*embed[\s>]|<\s*link[\s>].*\bhref\s*=|<\s*img[^>]+\b(onerror|onload)\s*=|document\.(cookie|write|location)|window\.(location|open)|eval\s*\(|String\.fromCharCode|atob\s*\(|btoa\s*\()",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    // Path traversal patterns
    private static readonly Regex PathTraversalRegex = new(
        @"(\.{2}[/\\]|%2e{2}[/\\%]|%252e{2}|\.{2}%2f|\.{2}%5c)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    // Command injection patterns
    private static readonly Regex CommandInjectionRegex = new(
        @"[;&|`$]\s*(cat|ls|dir|rm|del|wget|curl|bash|sh|cmd|powershell|nc|ncat|netcat|python|perl|ruby|php)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    // XXE patterns (XML External Entity)
    private static readonly Regex XxeRegex = new(
        @"<!DOCTYPE[^>]*\[|<!ENTITY|SYSTEM\s+[""']|PUBLIC\s+[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    // Open redirect patterns in parameter values
    private static readonly Regex OpenRedirectRegex = new(
        @"(redirect|return|next|url|goto|target|link|redir|destination|continue)\s*=\s*(https?://|//|\\\\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    // Maximum request body size to scan (10 KB) - avoid DoS from huge payloads
    private const int MaxBodyScanSize = 10240;

    // Headers to check for injection
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Referer", "Origin", "X-Forwarded-For", "X-Forwarded-Host", "User-Agent"
    };

    public RequestValidationMiddleware(RequestDelegate next, ILogger<RequestValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // 1. Validate path
        var path = context.Request.Path.ToString();
        if (IsMalicious(path, out var pathThreat))
        {
            _logger.LogWarning("Blocked {Threat} in path from IP {Ip}: {Path}", pathThreat, ip, path);
            await RejectRequest(context);
            return;
        }

        // 2. Validate query string
        var queryString = context.Request.QueryString.ToString();
        if (IsMalicious(queryString, out var qsThreat))
        {
            _logger.LogWarning("Blocked {Threat} in query from IP {Ip}: {Query}", qsThreat, ip, queryString);
            await RejectRequest(context);
            return;
        }

        // 3. Check for open redirect in query parameters
        if (!string.IsNullOrEmpty(queryString) && OpenRedirectRegex.IsMatch(Uri.UnescapeDataString(queryString)))
        {
            _logger.LogWarning("Blocked open redirect attempt from IP {Ip}: {Query}", ip, queryString);
            await RejectRequest(context);
            return;
        }

        // 4. Validate sensitive headers
        foreach (var headerName in SensitiveHeaders)
        {
            if (context.Request.Headers.TryGetValue(headerName, out var headerValue))
            {
                var value = headerValue.ToString();
                if (IsMalicious(value, out var headerThreat))
                {
                    _logger.LogWarning("Blocked {Threat} in header {Header} from IP {Ip}", headerThreat, headerName, ip);
                    await RejectRequest(context);
                    return;
                }
            }
        }

        // 5. Validate request body for non-GET/HEAD methods with content
        if (context.Request.ContentLength > 0 &&
            !HttpMethods.IsGet(context.Request.Method) &&
            !HttpMethods.IsHead(context.Request.Method) &&
            !HttpMethods.IsOptions(context.Request.Method))
        {
            // Only scan text-based content types (JSON, XML, form data)
            var contentType = context.Request.ContentType?.ToLowerInvariant() ?? "";
            if (contentType.Contains("json") || contentType.Contains("xml") ||
                contentType.Contains("form-urlencoded") || contentType.Contains("text/"))
            {
                context.Request.EnableBuffering();
                var bodySize = (int)Math.Min(context.Request.ContentLength ?? MaxBodyScanSize, MaxBodyScanSize);
                var buffer = new byte[bodySize];
                var bytesRead = await context.Request.Body.ReadAsync(buffer.AsMemory(0, bodySize));
                context.Request.Body.Position = 0;

                if (bytesRead > 0)
                {
                    var body = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (IsMalicious(body, out var bodyThreat))
                    {
                        _logger.LogWarning("Blocked {Threat} in request body from IP {Ip}, Path: {Path}",
                            bodyThreat, ip, path);
                        await RejectRequest(context);
                        return;
                    }

                    // XXE check specifically for XML content
                    if (contentType.Contains("xml") && XxeRegex.IsMatch(body))
                    {
                        _logger.LogWarning("Blocked XXE attack in XML body from IP {Ip}", ip);
                        await RejectRequest(context);
                        return;
                    }
                }
            }
        }

        await _next(context);
    }

    private static bool IsMalicious(string content, out string threatType)
    {
        threatType = string.Empty;
        if (string.IsNullOrEmpty(content))
            return false;

        // Decode to catch encoded attacks
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(content);
        }
        catch
        {
            // Malformed encoding can itself be an attack vector
            threatType = "MalformedEncoding";
            return true;
        }

        // Double-decode to catch double-encoded attacks
        string doubleDecoded;
        try
        {
            doubleDecoded = Uri.UnescapeDataString(decoded);
        }
        catch
        {
            doubleDecoded = decoded;
        }

        // Check both single and double-decoded content
        foreach (var text in new[] { decoded, doubleDecoded })
        {
            try
            {
                if (PathTraversalRegex.IsMatch(text))
                {
                    threatType = "PathTraversal";
                    return true;
                }

                if (SqlInjectionRegex.IsMatch(text))
                {
                    threatType = "SQLInjection";
                    return true;
                }

                if (XssRegex.IsMatch(text))
                {
                    threatType = "XSS";
                    return true;
                }

                if (CommandInjectionRegex.IsMatch(text))
                {
                    threatType = "CommandInjection";
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Regex timeout could indicate a ReDoS attack
                threatType = "RegexTimeout";
                return true;
            }
        }

        return false;
    }

    private static async Task RejectRequest(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message = "Invalid request" });
    }
}

/// <summary>
/// Middleware to enforce file upload security policies.
/// Protects against: Malicious file uploads, oversized files, path traversal via filenames.
/// </summary>
public class FileUploadValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<FileUploadValidationMiddleware> _logger;

    // Allowed file extensions (whitelist approach)
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
        ".txt", ".rtf", ".zip", ".xml", ".json"
    };

    // Dangerous file extensions (blacklist for double-check)
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".com", ".msi", ".ps1", ".psm1",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".scr", ".pif",
        ".hta", ".cpl", ".msp", ".mst", ".inf", ".reg", ".rgs",
        ".php", ".asp", ".aspx", ".jsp", ".py", ".rb", ".sh", ".bash",
        ".svg" // SVG can contain embedded scripts
    };

    // Maximum file size: 25 MB
    private const long MaxFileSize = 25 * 1024 * 1024;

    // Magic bytes for common safe file types
    private static readonly Dictionary<string, byte[][]> MagicBytes = new()
    {
        { ".pdf", new[] { new byte[] { 0x25, 0x50, 0x44, 0x46 } } }, // %PDF
        { ".jpg", new[] { new byte[] { 0xFF, 0xD8, 0xFF } } },
        { ".jpeg", new[] { new byte[] { 0xFF, 0xD8, 0xFF } } },
        { ".png", new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47 } } },
        { ".gif", new[] { new byte[] { 0x47, 0x49, 0x46, 0x38 } } }, // GIF8
        { ".zip", new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 }, new byte[] { 0x50, 0x4B, 0x05, 0x06 } } },
        { ".doc", new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0 } } },
        { ".xls", new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0 } } },
        { ".docx", new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } }, // ZIP-based
        { ".xlsx", new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } }, // ZIP-based
    };

    public FileUploadValidationMiddleware(RequestDelegate next, ILogger<FileUploadValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only check multipart form requests (file uploads)
        if (context.Request.HasFormContentType &&
            context.Request.ContentType?.Contains("multipart/form-data") == true)
        {
            var form = await context.Request.ReadFormAsync();
            foreach (var file in form.Files)
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                // Check file size
                if (file.Length > MaxFileSize)
                {
                    _logger.LogWarning("File upload rejected: size {Size} exceeds limit from IP {Ip}",
                        file.Length, ip);
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        message = $"File size exceeds the maximum allowed size of {MaxFileSize / (1024 * 1024)} MB"
                    });
                    return;
                }

                if (file.Length == 0)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { message = "Empty files are not allowed" });
                    return;
                }

                // Sanitize and validate filename
                var fileName = Path.GetFileName(file.FileName); // Strip path components
                if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    _logger.LogWarning("File upload rejected: invalid filename '{FileName}' from IP {Ip}",
                        file.FileName, ip);
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { message = "Invalid filename" });
                    return;
                }

                // Check extension against whitelist
                var extension = Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
                {
                    _logger.LogWarning("File upload rejected: extension '{Ext}' not allowed from IP {Ip}",
                        extension, ip);
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        message = $"File type '{extension}' is not allowed. Allowed types: {string.Join(", ", AllowedExtensions)}"
                    });
                    return;
                }

                // Double-check against dangerous extensions (catches double extensions like .pdf.exe)
                var allExtensions = GetAllExtensions(fileName);
                if (allExtensions.Any(ext => DangerousExtensions.Contains(ext)))
                {
                    _logger.LogWarning("File upload rejected: dangerous extension detected in '{FileName}' from IP {Ip}",
                        fileName, ip);
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { message = "File type not allowed" });
                    return;
                }

                // Validate magic bytes (file signature) for known types
                if (MagicBytes.TryGetValue(extension.ToLowerInvariant(), out var expectedSignatures))
                {
                    using var stream = file.OpenReadStream();
                    var headerBytes = new byte[8];
                    var read = await stream.ReadAsync(headerBytes);
                    stream.Position = 0;

                    if (read < 4 || !expectedSignatures.Any(sig => headerBytes.Take(sig.Length).SequenceEqual(sig)))
                    {
                        _logger.LogWarning(
                            "File upload rejected: magic bytes mismatch for '{FileName}' (claimed {Ext}) from IP {Ip}",
                            fileName, extension, ip);
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            message = "File content does not match the declared file type"
                        });
                        return;
                    }
                }
            }
        }

        await _next(context);
    }

    private static List<string> GetAllExtensions(string fileName)
    {
        var extensions = new List<string>();
        var name = fileName;
        while (true)
        {
            var ext = Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext)) break;
            extensions.Add(ext);
            name = Path.GetFileNameWithoutExtension(name);
        }
        return extensions;
    }
}

/// <summary>
/// Middleware to enforce request size limits and prevent payload-based DoS.
/// </summary>
public class RequestSizeLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestSizeLimitMiddleware> _logger;

    // 50 MB max for multipart (file uploads), 1 MB for everything else
    private const long MaxRequestSize = 1 * 1024 * 1024;
    private const long MaxMultipartRequestSize = 50 * 1024 * 1024;

    public RequestSizeLimitMiddleware(RequestDelegate next, ILogger<RequestSizeLimitMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var contentLength = context.Request.ContentLength;
        var isMultipart = context.Request.ContentType?.Contains("multipart/form-data") == true;
        var maxSize = isMultipart ? MaxMultipartRequestSize : MaxRequestSize;

        if (contentLength > maxSize)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _logger.LogWarning("Request size {Size} exceeds limit {Limit} from IP {Ip}",
                contentLength, maxSize, ip);
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { message = "Request body too large" });
            return;
        }

        await _next(context);
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

    public static IApplicationBuilder UseFileUploadValidation(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<FileUploadValidationMiddleware>();
    }

    public static IApplicationBuilder UseRequestSizeLimit(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RequestSizeLimitMiddleware>();
    }
}
