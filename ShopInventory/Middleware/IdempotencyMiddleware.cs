using System.Collections.Concurrent;
using System.Threading;
using ShopInventory.Models;

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
    private const int InProgressStatusCode = 102;

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

    // Exact "METHOD /path" endpoints that MUST carry an Idempotency-Key; keyless requests are
    // rejected with 428. Scoped to endpoints whose clients are known to send the key, so existing
    // callers that don't yet (sub-routes like {id}/approve, {id}/status, and other create
    // endpoints) keep working. Promote an endpoint here once its client sends the key.
    private static readonly HashSet<string> IdempotencyEnforcedEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
            "POST /api/salesorder",
            "POST /api/creditnote",
            "POST /api/invoice",
            "POST /api/incomingpayment",
            "POST /api/inventorytransfer",
    };

    // Parameterized create routes that MUST carry an Idempotency-Key, matched by prefix because the
    // path contains a variable segment (e.g. .../from-invoice/{invoiceId}). Keep these specific enough
    // that they cannot match an unrelated sub-route.
    private static readonly string[] IdempotencyEnforcedPrefixes =
    {
            "POST /api/creditnote/from-invoice/",
    };

    // These POST endpoints only validate/read state and do not create or mutate SAP documents.
    private static readonly HashSet<string> IdempotencySkippedEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
            "POST /api/invoice/validate",
            "POST /api/invoice/pods/validate-bulk",
    };

    // Endpoints whose handler owns idempotency, either through IIdempotencyRequestStore or through a
    // unique key on the document itself. Both persist enough to replay the real document; this
    // middleware cannot, because it only remembers a status code, so its replay answers with a bare
    // message and the caller never learns what was created. Because the key branch below triggers on
    // the header alone — for any path, enforced or not — letting it run here would shadow the handler
    // and lose exactly the document the retry wanted. Keyless enforcement below still applies: owning
    // the replay is not the same as being allowed to arrive without a key.
    private static readonly HashSet<string> HandlerOwnedIdempotencyEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
            // SalesOrderService.CreateAsync dedupes on ClientRequestId — which is the header, copied
            // across by the controller — against a unique index, and returns the existing order. That
            // guard also outranks this one: the dictionary here is per-process, so it cannot see a
            // duplicate that landed on another instance, and it treats a failed attempt as done.
            "POST /api/salesorder",
    };

    // The same, for routes whose path carries a variable segment. Matched on both ends because that
    // segment sits in the middle. A bare prefix of "POST /api/crates/transactions/" would also
    // swallow .../ensure-invoice, which owns no idempotency of its own.
    private static readonly (string Prefix, string Suffix)[] HandlerOwnedIdempotencyRoutes =
    {
            ("POST /api/crates/transactions/", "/pods"),
            ("POST /api/crates/transactions/", "/grvs"),
            // The POD upload deduplicates on the caller's external reference and, failing that, on
            // the content of the file itself, returning the attachment either way. Letting this
            // middleware answer instead would hand back a bare message and lose the attachment the
            // retry was asking for.
            ("POST /api/invoice/", "/pod"),
            // Delegates to UploadCratePodCommand — the same handler behind the crates route above,
            // so it owns idempotency for the same reason. "/pod" does not match this: the segment
            // ends "-pod", not "/pod".
            ("POST /api/invoice/", "/crate-pod"),
    };

    private static readonly string[] MobileSalesOrderCompatibilityRoles =
    {
            ApplicationRoles.Merchandiser,
            ApplicationRoles.SalesRep,
            ApplicationRoles.Adr,
            ApplicationRoles.Sales
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
        var endpointKey = $"{context.Request.Method} {path.TrimEnd('/')}";

        if (IdempotencySkippedEndpoints.Contains(endpointKey))
        {
            await _next(context);
            return;
        }

        // Handler-owned routes still face the keyless check below — they own the replay, not the
        // right to arrive without a key — so this only decides whether to reserve and replay here.
        var handlerOwnsIdempotency = IsHandlerOwnedIdempotency(endpointKey);

        // Get idempotency key from header
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            if (handlerOwnsIdempotency)
            {
                await _next(context);
                return;
            }

            // Clean up expired keys periodically
            CleanupExpiredKeys();

            var compositeKey = $"{context.Request.Method}:{path}:{idempotencyKey}";

            var reservation = (DateTime.UtcNow, InProgressStatusCode);
            if (!ProcessedKeys.TryAdd(compositeKey, reservation))
            {
                var existing = ProcessedKeys[compositeKey];
                _logger.LogWarning(
                    "Duplicate idempotent request blocked for path {Path} from IP {Ip}; existing request status is {Status}",
                    path,
                    context.Connection.RemoteIpAddress,
                    existing.StatusCode == InProgressStatusCode ? "InProgress" : "Completed");

                context.Response.StatusCode = existing.StatusCode == InProgressStatusCode
                    ? StatusCodes.Status409Conflict
                    : existing.StatusCode;
                context.Response.Headers["Idempotency-Replayed"] = "true";
                await context.Response.WriteAsJsonAsync(new
                {
                    message = existing.StatusCode == InProgressStatusCode
                        ? "An identical request is already in progress."
                        : "Duplicate request detected. This operation has already been processed."
                });
                return;
            }

            try
            {
                await _next(context);
                if (IsSuccess(context.Response.StatusCode))
                {
                    ProcessedKeys[compositeKey] = (DateTime.UtcNow, context.Response.StatusCode);
                }
                else
                {
                    // Only a success means a document exists, and only then is there anything to
                    // protect. Every other outcome created nothing, so it must remain retryable with
                    // the same stable key — a client that reuses its key (the mobile app keeps one
                    // per draft, for as long as the draft lives) would otherwise be locked out of
                    // its own order for the rest of the expiry window. Remembering a 4xx also costs
                    // the caller the reason it failed: the replay below answers with a bare
                    // "duplicate request" message in place of the real refusal.
                    ProcessedKeys.TryRemove(compositeKey, out _);
                }
            }
            catch
            {
                ProcessedKeys.TryRemove(compositeKey, out _);
                throw;
            }
        }
        else if (requiresIdempotency)
        {
            if (IdempotencyEnforcedEndpoints.Contains(endpointKey)
                || IdempotencyEnforcedPrefixes.Any(prefix => endpointKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                if (IsMobileSalesOrderCompatibilityRequest(context, endpointKey))
                {
                    _logger.LogWarning(
                        "Allowing keyless mobile sales order compatibility request for user {UserId} ({Roles}) from IP {Ip}; downstream ClientRequestId validation remains required",
                        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unknown",
                        string.Join(",", MobileSalesOrderCompatibilityRoles.Where(context.User.IsInRole)),
                        context.Connection.RemoteIpAddress);

                    await _next(context);
                    return;
                }

                _logger.LogWarning(
                    "Rejected keyless request to enforced idempotent endpoint {Endpoint} from IP {Ip}",
                    endpointKey, context.Connection.RemoteIpAddress);

                context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "This operation requires an Idempotency-Key header to prevent duplicate documents being posted to SAP.",
                    path = path.TrimEnd('/')
                });
                return;
            }

            // Other business-critical paths: warn loudly but don't block yet (clients not updated).
            // Nothing to warn about on a handler-owned route: it derives its own key from the body.
            if (!handlerOwnsIdempotency)
            {
                _logger.LogWarning("No Idempotency-Key provided for business-critical path {Path} from IP {Ip}",
                    path, context.Connection.RemoteIpAddress);
            }

            await _next(context);
        }
        else
        {
            await _next(context);
        }
    }

    private static bool IsSuccess(int statusCode)
        => statusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices;

    private static bool IsHandlerOwnedIdempotency(string endpointKey)
    {
        if (HandlerOwnedIdempotencyEndpoints.Contains(endpointKey))
            return true;

        foreach (var (prefix, suffix) in HandlerOwnedIdempotencyRoutes)
        {
            if (endpointKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && endpointKey.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                // Guard against prefix and suffix overlapping on a route that has neither a
                // variable segment nor anything between them.
                && endpointKey.Length > prefix.Length + suffix.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMobileSalesOrderCompatibilityRequest(HttpContext context, string endpointKey)
    {
        if (!string.Equals(endpointKey, "POST /api/salesorder", StringComparison.OrdinalIgnoreCase))
            return false;

        if (context.User.Identity?.IsAuthenticated != true)
            return false;

        return MobileSalesOrderCompatibilityRoles.Any(context.User.IsInRole);
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
