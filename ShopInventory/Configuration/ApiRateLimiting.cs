using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShopInventory.Features.RateLimit;
using ApiProblemDetails = ShopInventory.Common.ProblemDetails.ProblemDetailsDefaults;

namespace ShopInventory.Configuration;

/// <summary>
/// The API's rate limit policies, and the convention that gives each controller endpoint one.
/// </summary>
/// <remarks>
/// Here rather than in Program.cs so that a test can stand the same limiter up in a real pipeline:
/// the auth limit went unenforced for as long as it existed, and nothing short of sending requests
/// through the middleware shows whether it is.
/// </remarks>
public static class ApiRateLimiting
{
    public const string FixedPolicy = "fixed";
    public const string AuthPolicy = "auth";
    public const string ApiPolicy = "api";

    // The limits are read per request from IRateLimitConfigStore, which must be registered too.
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, SecuritySettings securitySettings)
    {
        var apiKeyRateLimitPartitions = securitySettings.ApiKeys
            .Where(key => key.IsActive && !string.IsNullOrWhiteSpace(key.Key))
            .ToDictionary(key => key.Key, key => key, StringComparer.Ordinal);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Default fixed window policy, partitioned by API key, authenticated user, or IP.
            options.AddPolicy(FixedPolicy, httpContext =>
            {
                var rateLimitSettings = GetLiveRateLimitSettings(httpContext);

                if (IsRateLimitWhitelisted(httpContext, rateLimitSettings))
                    return RateLimitPartition.GetNoLimiter(GetIpPartitionKey(httpContext, rateLimitSettings));

                return RateLimitPartition.GetFixedWindowLimiter(
                    GetClientPartitionKey(httpContext, rateLimitSettings, apiKeyRateLimitPartitions),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimitSettings.PermitLimit,
                        Window = TimeSpan.FromSeconds(rateLimitSettings.WindowSeconds),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = rateLimitSettings.QueueLimit
                    });
            });

            // Stricter rate limiting for authentication endpoints: sign-in, refresh, one-time codes.
            //
            // Partitioned by the caller's address and nothing else. Not by API key, because the Web
            // signs every browser in through the API with its one key, so a key partition is one
            // bucket for every Web user at once; and not by user, because nobody is signed in yet.
            // The Web forwards the browser's address (ClientAuditHeaders), which the forwarded
            // headers middleware puts on the connection, so each browser still gets its own bucket.
            // Only the IP whitelist exempts a caller here: an API key whitelist entry would exempt
            // every login the Web proxies.
            //
            // No queue: a request over the limit is refused at once. A queued one would wait out
            // the window and then be let through, which slows a password guesser without stopping it.
            options.AddPolicy(AuthPolicy, httpContext =>
            {
                var rateLimitSettings = GetLiveRateLimitSettings(httpContext);

                if (IsIpWhitelisted(httpContext, rateLimitSettings))
                    return RateLimitPartition.GetNoLimiter(GetIpPartitionKey(httpContext, rateLimitSettings));

                return RateLimitPartition.GetFixedWindowLimiter(
                    GetAuthPartitionKey(httpContext, rateLimitSettings),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimitSettings.AuthEndpointPermitLimit,
                        Window = TimeSpan.FromSeconds(rateLimitSettings.AuthEndpointWindowSeconds),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
            });

            // Sliding window for API endpoints
            options.AddPolicy(ApiPolicy, httpContext =>
            {
                var rateLimitSettings = GetLiveRateLimitSettings(httpContext);

                if (IsRateLimitWhitelisted(httpContext, rateLimitSettings))
                    return RateLimitPartition.GetNoLimiter(GetIpPartitionKey(httpContext, rateLimitSettings));

                var apiRateLimitValues = GetApiRateLimitValues(httpContext, rateLimitSettings);

                return RateLimitPartition.GetSlidingWindowLimiter(
                    GetApiPartitionKey(httpContext, rateLimitSettings, apiKeyRateLimitPartitions),
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = apiRateLimitValues.PermitLimit,
                        Window = TimeSpan.FromSeconds(apiRateLimitValues.WindowSeconds),
                        SegmentsPerWindow = 4,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = apiRateLimitValues.QueueLimit
                    });
            });

            // Custom response for rate limit exceeded
            options.OnRejected = async (context, cancellationToken) =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("ShopInventory.RateLimiting");
                var rateLimitSettings = GetLiveRateLimitSettings(context.HttpContext);
                var isAuth = IsAuthEndpoint(context.HttpContext);
                var retryAfterSeconds = isAuth
                    ? rateLimitSettings.AuthEndpointWindowSeconds
                    : GetApiRateLimitValues(context.HttpContext, rateLimitSettings).WindowSeconds;

                logger.LogWarning("Rate limit exceeded for client: {ClientPartition}, IP: {IpAddress}, Path: {Path}",
                    isAuth
                        ? GetAuthPartitionKey(context.HttpContext, rateLimitSettings)
                        : GetApiPartitionKey(context.HttpContext, rateLimitSettings, apiKeyRateLimitPartitions),
                    context.HttpContext.Connection.RemoteIpAddress,
                    context.HttpContext.Request.Path.Value);

                context.HttpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();

                var problemDetailsService = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                var problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests.",
                    Type = ApiProblemDetails.GetType(StatusCodes.Status429TooManyRequests),
                    Detail = "Too many requests. Please try again later."
                };
                problemDetails.Extensions["retryAfter"] = retryAfterSeconds;

                await ApiProblemDetails.WriteAsync(
                    problemDetailsService,
                    context.HttpContext,
                    null,
                    problemDetails,
                    cancellationToken);
            };
        });

        return services;
    }

    /// <summary>
    /// Gives every endpoint that does not name a rate limit policy of its own the
    /// <paramref name="policy"/> one.
    /// </summary>
    /// <remarks>
    /// <c>RequireRateLimiting</c> cannot be used for this. The rate limiting middleware reads the
    /// last <see cref="EnableRateLimitingAttribute"/> in an endpoint's metadata, and a convention's
    /// metadata is added after the action's attributes, so <c>MapControllers().RequireRateLimiting("api")</c>
    /// replaced every <c>[EnableRateLimiting("auth")]</c> with "api" and the auth limit was never
    /// applied. This adds the default only where the action has said nothing, and leaves
    /// <see cref="DisableRateLimitingAttribute"/> alone too.
    /// </remarks>
    public static TBuilder RequireDefaultRateLimiting<TBuilder>(this TBuilder builder, string policy)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint =>
        {
            if (!endpoint.Metadata.Any(item => item is EnableRateLimitingAttribute or DisableRateLimitingAttribute))
                endpoint.Metadata.Add(new EnableRateLimitingAttribute(policy));
        });

        return builder;
    }

    internal static string GetAuthPartitionKey(HttpContext httpContext, RateLimitSettings settings) =>
        $"{GetIpPartitionKey(httpContext, settings)}|auth";

    private static bool IsAuthEndpoint(HttpContext httpContext) =>
        httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == AuthPolicy;

    private static string GetClientPartitionKey(
        HttpContext httpContext,
        RateLimitSettings settings,
        IReadOnlyDictionary<string, ApiKeyConfig> apiKeyRateLimitPartitions)
    {
        if (httpContext.Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeaderValues))
        {
            var apiKey = apiKeyHeaderValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(apiKey)
                && apiKeyRateLimitPartitions.TryGetValue(apiKey, out var keyConfig)
                && keyConfig.ExpiresAt.HasValue
                && keyConfig.ExpiresAt.Value >= DateTime.UtcNow)
            {
                var keyName = string.IsNullOrWhiteSpace(keyConfig.Name) ? "unnamed" : keyConfig.Name;
                return $"{settings.Fingerprint()}|api-key:{keyName}";
            }
        }

        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue("sub")
                ?? httpContext.User.Identity.Name;

            if (!string.IsNullOrWhiteSpace(userId))
                return $"{settings.Fingerprint()}|user:{userId}";
        }

        if (settings.EnableIpRateLimiting)
            return GetIpPartitionKey(httpContext, settings);

        return $"{settings.Fingerprint()}|anonymous";
    }

    private static string GetApiPartitionKey(
        HttpContext httpContext,
        RateLimitSettings settings,
        IReadOnlyDictionary<string, ApiKeyConfig> apiKeyRateLimitPartitions)
    {
        var clientPartition = GetClientPartitionKey(httpContext, settings, apiKeyRateLimitPartitions);
        return IsHighThroughputApiPath(httpContext.Request.Path)
            ? $"{clientPartition}:bulk"
            : clientPartition;
    }

    private static (int PermitLimit, int WindowSeconds, int QueueLimit) GetApiRateLimitValues(
        HttpContext httpContext,
        RateLimitSettings settings)
    {
        if (!IsHighThroughputApiPath(httpContext.Request.Path))
            return (settings.PermitLimit, settings.WindowSeconds, settings.QueueLimit);

        return (
            PermitLimit: Math.Max(settings.PermitLimit * 50, 5000),
            WindowSeconds: Math.Max(settings.WindowSeconds * 10, 600),
            QueueLimit: Math.Max(settings.QueueLimit * 50, 200));
    }

    private static string GetIpPartitionKey(HttpContext httpContext, RateLimitSettings settings)
    {
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        var key = string.IsNullOrWhiteSpace(ipAddress) ? "ip:unknown" : $"ip:{ipAddress}";
        return $"{settings.Fingerprint()}|{key}";
    }

    private static bool IsHighThroughputApiPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.StartsWith("/api/notification", StringComparison.OrdinalIgnoreCase)
            || (value.StartsWith("/api/invoice/", StringComparison.OrdinalIgnoreCase)
                && value.EndsWith("/pod", StringComparison.OrdinalIgnoreCase));
    }

    // The limits in force right now, rather than the ones configured at startup, so a change made
    // through PUT /api/RateLimit/config takes effect without a restart. Reads a cached snapshot: no
    // database work happens here.
    private static RateLimitSettings GetLiveRateLimitSettings(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IRateLimitConfigStore>().Current;

    private static bool IsRateLimitWhitelisted(HttpContext httpContext, RateLimitSettings settings)
    {
        if (settings.ApiKeyWhitelist.Count > 0
            && httpContext.Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeaderValues))
        {
            var apiKey = apiKeyHeaderValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(apiKey)
                && settings.ApiKeyWhitelist.Contains(apiKey, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return IsIpWhitelisted(httpContext, settings);
    }

    private static bool IsIpWhitelisted(HttpContext httpContext, RateLimitSettings settings)
    {
        if (settings.IpWhitelist.Count == 0)
            return false;

        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        return !string.IsNullOrWhiteSpace(ipAddress)
            && settings.IpWhitelist.Contains(ipAddress, StringComparer.OrdinalIgnoreCase);
    }
}
