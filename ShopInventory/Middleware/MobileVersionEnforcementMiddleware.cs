using ShopInventory.Common.Errors;
using ShopInventory.Features.AppVersion;

namespace ShopInventory.Middleware;

public sealed class MobileVersionEnforcementMiddleware(
    RequestDelegate next,
    ILogger<MobileVersionEnforcementMiddleware> logger,
    IMobileVersionPolicyEvaluator evaluator
)
{
    private const string PlatformHeaderName = "X-App-Platform";
    private const string VersionHeaderName = "X-App-Version";
    private const string AndroidPlatform = "android";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        if (IsExemptPath(path))
        {
            await next(context);
            return;
        }

        var platform = context.Request.Headers[PlatformHeaderName].FirstOrDefault();
        if (!string.Equals(platform?.Trim(), AndroidPlatform, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var currentVersion = context.Request.Headers[VersionHeaderName].FirstOrDefault();
        var evaluation = evaluator.Evaluate(platform, currentVersion);
        if (!evaluation.PolicyApplies)
        {
            await next(context);
            return;
        }

        if (!evaluation.HasValidVersionMetadata && evaluation.RequireHeaders)
        {
            logger.LogWarning(
                "Rejected Android request with invalid app version metadata on {Path}. Platform={Platform}, Version={Version}",
                context.Request.Path,
                platform,
                currentVersion);

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                code = Errors.Auth.InvalidAppVersionMetadata.Code,
                message = Errors.Auth.InvalidAppVersionMetadata.Description,
                requiredHeaders = new[] { PlatformHeaderName, VersionHeaderName }
            }, context.RequestAborted);
            return;
        }

        if (!evaluation.ShouldForceUpgrade)
        {
            await next(context);
            return;
        }

        logger.LogWarning(
            "Blocked Android request from unsupported app version {Version} on {Path}",
            evaluation.CurrentVersion,
            context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        await context.Response.WriteAsJsonAsync(new
        {
            code = Errors.Auth.AppVersionBlocked.Code,
            message = evaluation.Message ?? Errors.Auth.AppVersionBlocked.Description,
            status = evaluation.Status,
            currentVersion = evaluation.CurrentVersion,
            latestVersion = evaluation.LatestVersion,
            recommendedVersion = evaluation.RecommendedVersion,
            minimumSupportedVersion = evaluation.MinimumSupportedVersion,
            downloadUrl = evaluation.DownloadUrl,
            releaseNotes = evaluation.ReleaseNotes,
            shouldForceUpgrade = evaluation.ShouldForceUpgrade,
            checkedAtUtc = evaluation.CheckedAtUtc
        }, context.RequestAborted);
    }

    private static bool IsExemptPath(string path)
    {
        return path == "/"
               || path.StartsWith("/swagger", StringComparison.Ordinal)
               || path.StartsWith("/hubs/notifications", StringComparison.Ordinal)
               || path.StartsWith("/api/health", StringComparison.Ordinal)
               || path.StartsWith("/api/appversion/mobile", StringComparison.Ordinal);
    }
}