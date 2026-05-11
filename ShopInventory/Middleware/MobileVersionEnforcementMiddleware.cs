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
    private const string DeviceModelHeaderName = "X-Device-Model";
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
        var currentVersion = context.Request.Headers[VersionHeaderName].FirstOrDefault();
        var deviceModel = context.Request.Headers[DeviceModelHeaderName].FirstOrDefault();
        var explicitlyTargetsAndroid = string.Equals(platform?.Trim(), AndroidPlatform, StringComparison.OrdinalIgnoreCase);
        var hasMobileMetadata = !string.IsNullOrWhiteSpace(currentVersion)
            || !string.IsNullOrWhiteSpace(deviceModel);

        if (!explicitlyTargetsAndroid && !string.IsNullOrWhiteSpace(platform))
        {
            await next(context);
            return;
        }

        if (!explicitlyTargetsAndroid && !hasMobileMetadata)
        {
            await next(context);
            return;
        }

        var evaluation = evaluator.Evaluate(AndroidPlatform, currentVersion);
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

            await WriteInvalidMetadataResponseAsync(context);
            return;
        }

        if (evaluation.ShouldForceUpgrade)
        {
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
            return;
        }

        if (!explicitlyTargetsAndroid && evaluation.RequireHeaders)
        {
            logger.LogWarning(
                "Rejected request with incomplete Android app version metadata on {Path}. Platform={Platform}, Version={Version}, DeviceModel={DeviceModel}",
                context.Request.Path,
                platform,
                currentVersion,
                deviceModel);

            await WriteInvalidMetadataResponseAsync(context);
            return;
        }

        await next(context);
    }

    private static bool IsExemptPath(string path)
    {
        return path == "/"
               || path.StartsWith("/swagger", StringComparison.Ordinal)
               || path.StartsWith("/hubs/notifications", StringComparison.Ordinal)
               || path.StartsWith("/api/health", StringComparison.Ordinal)
               || path.StartsWith("/api/appversion/mobile", StringComparison.Ordinal);
    }

    private static Task WriteInvalidMetadataResponseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(new
        {
            code = Errors.Auth.InvalidAppVersionMetadata.Code,
            message = Errors.Auth.InvalidAppVersionMetadata.Description,
            requiredHeaders = new[] { PlatformHeaderName, VersionHeaderName }
        }, context.RequestAborted);
    }
}