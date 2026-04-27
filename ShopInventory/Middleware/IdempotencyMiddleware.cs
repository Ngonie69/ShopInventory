using System.Collections.Concurrent;
using System.Threading;

namespace ShopInventory.Middleware;

/// <summary>
/// Middleware to prevent duplicate form/API submissions using idempotency keys.
/// Protects against: Double-submit attacks, duplicate invoice posting, race conditions.
/// 
/// Usage: Clients send an "Idempotency-Key" header with POST/PUT/PATCH requests.
/// If the same key is seen within the expiration window, the request is rejected.
/// </summary>
public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;
    private static readonly ConcurrentDictionary<string, (DateTime Timestamp, int StatusCode)> ProcessedKeys = new();
    private static long _lastCleanupTicks = DateTime.UtcNow.Ticks;

    // How long to remember idempotency keys (default 60 minutes)
    private const int DefaultExpirationMinutes = 60;

    // Paths that require idempotency (write operations on SAP or business-critical endpoints)
    private static readonly string[] IdempotencyRequiredPaths =
    {
            "/api/merchandiser/mobile/order",
            "/api/invoice", "/api/salesorder", "/api/creditnote",
            "/api/incomingpayment", "/api/inventorytransfer", "/api/payment",
            "/api/purchaseorder", "/api/purchaseinvoice", "/api/goodsreceiptpurchaseorder",
            "/api/quotation", "/api/purchasequotation"
    };

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only check write methods
        if (!HttpMethods.IsPost(context.Request.Method) &&
            !HttpMethods.IsPut(context.Request.Method) &&
            !HttpMethods.IsPatch(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // Check if the path requires idempotency
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        var requiresIdempotency = IdempotencyRequiredPaths.Any(p => path.StartsWith(p));

        // Get idempotency key from header
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            // Clean up expired keys periodically
            CleanupExpiredKeys();

            var compositeKey = $"{context.Request.Method}:{path}:{idempotencyKey}";

            if (ProcessedKeys.TryGetValue(compositeKey, out var existing))
            {
                _logger.LogWarning("Duplicate request blocked - Idempotency-Key: {Key}, Path: {Path}, IP: {Ip}",
                    idempotencyKey, path, context.Connection.RemoteIpAddress);

                context.Response.StatusCode = existing.StatusCode;
                context.Response.Headers["Idempotency-Replayed"] = "true";
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Duplicate request detected. This operation has already been processed.",
                    idempotencyKey
                });
                return;
            }

            // Process the request and record the result
            await _next(context);

            // Store the key with the response status
            ProcessedKeys.TryAdd(compositeKey, (DateTime.UtcNow, context.Response.StatusCode));
        }
        else if (requiresIdempotency)
        {
            // Warn but don't block - idempotency key recommended for critical operations
            _logger.LogDebug("No Idempotency-Key provided for business-critical path {Path} from IP {Ip}",
                path, context.Connection.RemoteIpAddress);
            await _next(context);
        }
        else
        {
            await _next(context);
        }
    }

    private static void CleanupExpiredKeys()
    {
        var now = DateTime.UtcNow;
        var lastTicks = Interlocked.Read(ref _lastCleanupTicks);
        if ((now - new DateTime(lastTicks, DateTimeKind.Utc)).TotalMinutes < 10)
            return;

        // Atomic compare-and-swap to prevent concurrent cleanup runs
        if (Interlocked.CompareExchange(ref _lastCleanupTicks, now.Ticks, lastTicks) != lastTicks)
            return;

        var cutoff = now.AddMinutes(-DefaultExpirationMinutes);
        foreach (var kvp in ProcessedKeys)
        {
            if (kvp.Value.Timestamp < cutoff)
                ProcessedKeys.TryRemove(kvp.Key, out _);
        }
    }
}

/// <summary>
/// Extension method for idempotency middleware
/// </summary>
public static class IdempotencyMiddlewareExtensions
{
    public static IApplicationBuilder UseIdempotency(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<IdempotencyMiddleware>();
    }
}
