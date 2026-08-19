using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.ProblemDetails;

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

        // Prevent clickjacking - DENY for all API endpoints, allow framing for Swagger
        if (!isSwaggerPath)
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
        // Swagger needs relaxed CSP for inline scripts and styles to render
        if (isSwaggerPath)
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

        // Cache-Control: allow browser caching for safe GET read endpoints,
        // but prevent caching for auth/sensitive/mutation requests
        if (!headers.ContainsKey("Cache-Control"))
        {
            var method = context.Request.Method;
            var isSafeReadEndpoint = HttpMethods.IsGet(method) && !IsSensitivePath(path);

            if (isSafeReadEndpoint)
            {
                // Allow private (browser-only) caching for 60s on safe GET endpoints
                headers["Cache-Control"] = "private, max-age=60, must-revalidate";
            }
            else
            {
                headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                headers["Pragma"] = "no-cache";
                headers["Expires"] = "0";
            }
        }

        // Cross-Origin policies (relaxed for Swagger to load UI assets)
        if (isSwaggerPath)
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

    /// <summary>
    /// Paths that contain sensitive data and must never be cached by browsers.
    /// </summary>
    private static bool IsSensitivePath(string path)
    {
        return path.StartsWith("/api/auth") ||
               path.StartsWith("/api/user") ||
               path.StartsWith("/api/password") ||
               path.StartsWith("/api/customerportal/auth") ||
               path.StartsWith("/api/backup") ||
               path.StartsWith("/swagger");
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

    // Per-pattern match budget.
    //
    // This is a CPU bound, not an attack signal: it caps what any single request can spend inside the
    // regex engine, so a pathological input cannot pin a worker thread. Hitting it is NOT treated as
    // evidence of an attack — see IsMalicious, which fails open on a timeout.
    //
    // Keep it small. A warmed-up match on these patterns costs microseconds, and a request is scanned
    // at up to a dozen points (path, query, five headers, body) times two decodings times six
    // patterns, so the budget multiplies out into the worst case an attacker can force.
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(200);

    // SQL Injection patterns.
    //
    // The "--" comment marker only counts when it is not wedged between two word characters. Every
    // real payload has it at a boundary — "admin'--", "1 OR 1=1 --", "'; DROP TABLE x;--" — whereas
    // opaque base64url tokens (SignalR connection ids, FCM tokens, JWTs) contain "--" between letters
    // and digits as a matter of course. Matching it bare blocked a live SignalR request
    // (?id=F9--6N_Qk3Nu71I4wABSTA) and dropped the user's hub connection.
    private static readonly Regex SqlInjectionRegex = new(
        @"(\b(union\s+(all\s+)?select|insert\s+into|delete\s+from|update\s+.*set|drop\s+(table|database|index)|alter\s+table|create\s+(table|database)|exec(\s|\()|execute(\s|\()|xp_|sp_|0x[0-9a-f]+)\b)|('(\s|%20)*(or|and)(\s|%20)*')|((?<![A-Za-z0-9_])-{2}|-{2}(?![A-Za-z0-9_]))|(/\*.*\*/)|(\b(or|and)\b\s+\d+\s*=\s*\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, PatternTimeout);

    // XSS patterns
    private static readonly Regex XssRegex = new(
        @"(<\s*script[\s>]|javascript\s*:|on(click|error|load|mouseover|focus|blur|submit|change|keyup|keydown|input)\s*=|<\s*iframe[\s>]|<\s*object[\s>]|<\s*embed[\s>]|<\s*link[\s>].*\bhref\s*=|<\s*img[^>]+\b(onerror|onload)\s*=|document\.(cookie|write|location)|window\.(location|open)|eval\s*\(|String\.fromCharCode|atob\s*\(|btoa\s*\()",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, PatternTimeout);

    // Path traversal patterns
    private static readonly Regex PathTraversalRegex = new(
        @"(\.{2}[/\\]|%2e{2}[/\\%]|%252e{2}|\.{2}%2f|\.{2}%5c)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, PatternTimeout);

    // Command injection patterns
    private static readonly Regex CommandInjectionRegex = new(
        @"[;&|`$]\s*(cat|ls|dir|rm|del|wget|curl|bash|sh|cmd|powershell|nc|ncat|netcat|python|perl|ruby|php)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, PatternTimeout);

    // XXE patterns (XML External Entity)
    private static readonly Regex XxeRegex = new(
        @"<!DOCTYPE[^>]*\[|<!ENTITY|SYSTEM\s+[""']|PUBLIC\s+[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, PatternTimeout);

    // Open redirect patterns in parameter values
    private static readonly Regex OpenRedirectRegex = new(
        @"(redirect|return|next|url|goto|target|link|redir|destination|continue)\s*=\s*(https?://|//|\\\\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, PatternTimeout);

    /// <summary>
    /// One heuristic content pattern and the threat name reported when it matches.
    /// </summary>
    internal readonly record struct ThreatPattern(Regex Pattern, string Threat);

    // The content patterns IsMalicious runs, in order.
    private static readonly ThreatPattern[] ContentPatterns =
    {
        new(PathTraversalRegex, "PathTraversal"),
        new(SqlInjectionRegex, "SQLInjection"),
        new(XssRegex, "XSS"),
        new(CommandInjectionRegex, "CommandInjection"),
    };

    static RequestValidationMiddleware()
    {
        // Warm every pattern at type-initialization time, which happens while Program.cs builds the
        // pipeline — before the first request arrives.
        //
        // RegexOptions.Compiled defers IL emit and JIT to the first IsMatch call, and that work runs
        // inside that call's own timeout window. Measured on an idle machine, the first match on the
        // SQL pattern costs ~93 ms against a 200 ms budget while every later match costs ~0.004 ms.
        // A loaded server only has to add ~107 ms of scheduling delay for a benign first request to
        // trip the timeout. Paying the cost once here takes it out of the request path entirely.
        foreach (var pattern in new[]
                 {
                     PathTraversalRegex, SqlInjectionRegex, XssRegex,
                     CommandInjectionRegex, XxeRegex, OpenRedirectRegex
                 })
        {
            try
            {
                pattern.IsMatch("warm-up");
            }
            catch (RegexMatchTimeoutException)
            {
                // The point of the call is the compile, not the result.
            }
        }
    }

    // Maximum request body size to scan (10 KB) - avoid DoS from huge payloads
    private const int MaxBodyScanSize = 10240;

    // Paths whose request bodies contain credentials/secrets and should NOT be scanned
    // (passwords with special characters trigger false-positive injection detections)
    private static readonly string[] BodyScanExcludedPaths =
    {
        "/api/user/",          // admin password reset: /api/user/{id}/change-password
        "/api/auth/login",     // login credentials
        "/api/auth/register",  // registration passwords
        "/api/password/",      // self-service password change/reset
        "/api/customerportal/auth", // customer portal login/password
        // FCM tokens are opaque, high-entropy credentials that can legitimately contain
        // substrings such as "--" or "sp_". The endpoint is authenticated and its DTO applies
        // an explicit token-character allowlist, so generic SQL-pattern scanning is harmful here.
        "/api/pushnotification/register",
        "/api/pushnotification/unregister"
    };

    // Paths whose query strings are not ours to read. SignalR puts its own opaque connection token
    // in ?id= on every transport request after negotiate, and it validates that token itself; a
    // pattern scan there can only ever produce false positives, and each one severs a live hub
    // connection. The path is still checked.
    private static readonly string[] QueryScanExcludedPaths =
    {
        "/hubs/"
    };

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
        if (ScanRequestPart(path, "path", ip, out var pathThreat))
        {
            _logger.LogWarning("Blocked {Threat} in path from IP {Ip}: {Path}", pathThreat, ip, path);
            await RejectRequest(context);
            return;
        }

        // 2. Validate query string
        var queryString = context.Request.QueryString.ToString();
        var skipQueryScan = QueryScanExcludedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (!skipQueryScan && ScanRequestPart(queryString, "query string", ip, out var qsThreat))
        {
            _logger.LogWarning("Blocked {Threat} in query from IP {Ip}: {Query}", qsThreat, ip, queryString);
            await RejectRequest(context);
            return;
        }

        // 3. Check for open redirect in query parameters
        if (!skipQueryScan && !string.IsNullOrEmpty(queryString) &&
            MatchesOrFailsOpen(OpenRedirectRegex, Uri.UnescapeDataString(queryString), "open redirect", "query string", ip))
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
                if (ScanRequestPart(value, $"header {headerName}", ip, out var headerThreat))
                {
                    _logger.LogWarning("Blocked {Threat} in header {Header} from IP {Ip}", headerThreat, headerName, ip);
                    await RejectRequest(context);
                    return;
                }
            }
        }

        // 5. Validate request body for non-GET/HEAD methods with content
        // Skip body scanning for endpoints that contain credentials (passwords trigger false positives)
        var skipBodyScan = BodyScanExcludedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (!skipBodyScan &&
            context.Request.ContentLength > 0 &&
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
                    if (ScanRequestPart(body, "body", ip, out var bodyThreat))
                    {
                        _logger.LogWarning("Blocked {Threat} in request body from IP {Ip}, Path: {Path}",
                            bodyThreat, ip, path);
                        await RejectRequest(context);
                        return;
                    }

                    // XXE check specifically for XML content
                    if (contentType.Contains("xml") &&
                        MatchesOrFailsOpen(XxeRegex, body, "XXE", "body", ip))
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

    /// <summary>
    /// Scans one part of the request, logging — but not rejecting on — a pattern that ran out of
    /// its match budget. See the fail-open note in <see cref="IsMalicious(string, out string, out bool, ThreatPattern[])"/>.
    /// </summary>
    private bool ScanRequestPart(string content, string part, string ip, out string threatType)
    {
        var malicious = IsMalicious(content, out threatType, out var scanIncomplete);

        if (scanIncomplete)
        {
            _logger.LogWarning(
                "Request validation pattern scan ran out of its {TimeoutMs} ms budget on the {Part} from IP {Ip}; " +
                "the request was allowed through. Repeated occurrences mean either a ReDoS probe or a host too " +
                "loaded to finish a match that normally takes microseconds.",
                PatternTimeout.TotalMilliseconds, part, ip);
        }

        return malicious;
    }

    /// <summary>
    /// Runs a single pattern that is not part of <see cref="ContentPatterns"/>, applying the same
    /// fail-open rule: a match budget overrun is logged and the request is allowed to continue.
    /// </summary>
    private bool MatchesOrFailsOpen(Regex pattern, string text, string threat, string part, string ip)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            _logger.LogWarning(
                "{Threat} pattern ran out of its {TimeoutMs} ms budget on the {Part} from IP {Ip}; " +
                "the request was allowed through.",
                threat, PatternTimeout.TotalMilliseconds, part, ip);
            return false;
        }
    }

    internal static bool IsMalicious(string content, out string threatType) =>
        IsMalicious(content, out threatType, out _);

    /// <summary>
    /// Runs the heuristic content patterns over <paramref name="content"/> and its decodings.
    /// </summary>
    /// <param name="content">The request fragment to scan.</param>
    /// <param name="threatType">The name of the pattern that matched, or empty if none did.</param>
    /// <param name="scanIncomplete">
    /// True when at least one pattern ran out of its match budget, so the scan cannot say whether
    /// that pattern would have matched. Callers should log this; it is not a reason to reject.
    /// </param>
    /// <param name="patterns">The patterns to run. Defaults to <see cref="ContentPatterns"/>; tests override it.</param>
    internal static bool IsMalicious(
        string content,
        out string threatType,
        out bool scanIncomplete,
        ThreatPattern[]? patterns = null)
    {
        threatType = string.Empty;
        scanIncomplete = false;
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
            foreach (var (pattern, threat) in patterns ?? ContentPatterns)
            {
                // Each pattern gets its own try, so one that runs out of budget does not blind the
                // ones after it. A payload that stalls the path-traversal pattern must not thereby
                // skip the SQL, XSS and command-injection checks.
                try
                {
                    if (pattern.IsMatch(text))
                    {
                        threatType = threat;
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // Fail open. A timeout says the match did not finish in its budget; it does not
                    // say the content was hostile. Every one of these patterns is a heuristic sitting
                    // in front of parameterized queries, so the cost of guessing wrong is asymmetric:
                    // guessing "attack" rejects a paying customer's request with a 400, while guessing
                    // "unknown" leaves the request to the real controls behind this middleware.
                    //
                    // Blocking would not buy ReDoS protection either. The CPU is already spent by the
                    // time the exception is raised — the timeout itself is what bounds the damage, and
                    // it does that whichever way this branch goes.
                    //
                    // The caller logs it. A steady stream of these is a signal worth alerting on: it
                    // means either someone probing for a stall or a machine too loaded to finish a
                    // microsecond match inside its budget.
                    scanIncomplete = true;
                }
            }
        }

        return false;
    }

    private static async Task RejectRequest(HttpContext context)
    {
        await SecurityProblemDetailsWriter.WriteAsync(
            context,
            StatusCodes.Status400BadRequest,
            "Invalid request.",
            "The request was rejected by request validation.");
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
                    await SecurityProblemDetailsWriter.WriteAsync(
                        context,
                        StatusCodes.Status413PayloadTooLarge,
                        "Uploaded file is too large.",
                        $"File size exceeds the maximum allowed size of {MaxFileSize / (1024 * 1024)} MB.");
                    return;
                }

                if (file.Length == 0)
                {
                    await SecurityProblemDetailsWriter.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "Invalid file upload.",
                        "Empty files are not allowed.");
                    return;
                }

                // Sanitize and validate filename
                var fileName = Path.GetFileName(file.FileName); // Strip path components
                if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    _logger.LogWarning("File upload rejected: invalid filename '{FileName}' from IP {Ip}",
                        file.FileName, ip);
                    await SecurityProblemDetailsWriter.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "Invalid file upload.",
                        "Invalid filename.");
                    return;
                }

                // Check extension against whitelist
                var extension = Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
                {
                    _logger.LogWarning("File upload rejected: extension '{Ext}' not allowed from IP {Ip}",
                        extension, ip);
                    await SecurityProblemDetailsWriter.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "Invalid file upload.",
                        $"File type '{extension}' is not allowed. Allowed types: {string.Join(", ", AllowedExtensions)}.");
                    return;
                }

                // Double-check against dangerous extensions (catches double extensions like .pdf.exe)
                var allExtensions = GetAllExtensions(fileName);
                if (allExtensions.Any(ext => DangerousExtensions.Contains(ext)))
                {
                    _logger.LogWarning("File upload rejected: dangerous extension detected in '{FileName}' from IP {Ip}",
                        fileName, ip);
                    await SecurityProblemDetailsWriter.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "Invalid file upload.",
                        "File type not allowed.");
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
                        await SecurityProblemDetailsWriter.WriteAsync(
                            context,
                            StatusCodes.Status400BadRequest,
                            "Invalid file upload.",
                            "File content does not match the declared file type.");
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
/// <summary>
/// Declares a per-endpoint request body size limit that <see cref="RequestSizeLimitMiddleware"/>
/// enforces. Unlike the built-in [RequestSizeLimit] attribute, this is metadata only — it does
/// not register an MVC resource filter, so it avoids the "IHttpRequestBodySizeFeature ... is
/// read-only" warning that filter logs on every request under IIS in-process hosting (where the
/// server body-size feature cannot be set per request). Hard enforcement of the overall ceiling
/// is done at the server level (IIS/Kestrel MaxRequestBodySize, configured in Program.cs).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class MaxRequestBodySizeAttribute : Attribute, IRequestSizeLimitMetadata
{
    public MaxRequestBodySizeAttribute(long maxRequestBodySize) => MaxRequestBodySize = maxRequestBodySize;

    public long? MaxRequestBodySize { get; }
}

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
        var endpointMaxSize = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;
        var maxSize = endpointMaxSize ?? (isMultipart ? MaxMultipartRequestSize : MaxRequestSize);

        if (contentLength > maxSize)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _logger.LogWarning("Request size {Size} exceeds limit {Limit} from IP {Ip}",
                contentLength, maxSize, ip);
            await SecurityProblemDetailsWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "Request body too large.",
                "Request body too large.");
            return;
        }

        await _next(context);
    }
}

internal static class SecurityProblemDetailsWriter
{
    public static ValueTask<bool> WriteAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail)
    {
        var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = ProblemDetailsDefaults.GetType(statusCode),
            Detail = detail
        };

        return ProblemDetailsDefaults.WriteAsync(
            problemDetailsService,
            context,
            null,
            problemDetails,
            context.RequestAborted);
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
