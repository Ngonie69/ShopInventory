using ShopInventory.Common.Errors;
using ShopInventory.Features.AppVersion;

namespace ShopInventory.Middleware;

public sealed class MobileVersionEnforcementMiddleware(
    RequestDelegate next,
    ILogger<MobileVersionEnforcementMiddleware> logger,
    IMobileVersionPolicyEvaluator evaluator
)
{
    private const string PlatformHeaderName = MobileClientRequest.PlatformHeaderName;
    private const string VersionHeaderName = MobileClientRequest.VersionHeaderName;
    private const string AndroidPlatform = MobileClientRequest.AndroidPlatform;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        if (IsExemptPath(path))
        {
            await next(context);
            return;
        }

        // Shared with the maintenance lockout rather than repeated here: two gates that disagree
        // about what counts as a phone would leave a hole in whichever of them is stricter.
        var client = MobileClientRequest.FromHeaders(context.Request.Headers);
        var platform = client.Platform;
        var appId = client.AppId;
        var currentVersion = client.Version;
        var deviceModel = client.DeviceModel;
        var explicitlyTargetsAndroid = client.NamesAndroid;

        if (!client.IsMobileApp)
        {
            await next(context);
            return;
        }

        var evaluation = evaluator.Evaluate(appId, AndroidPlatform, currentVersion);
        if (!evaluation.PolicyApplies)
        {
            await next(context);
            return;
        }

        if (!evaluation.HasValidVersionMetadata && evaluation.RequireHeaders)
        {
            logger.LogWarning(
                "Rejected Android request with invalid app version metadata on {Path}. AppId={AppId}, Platform={Platform}, Version={Version}",
                context.Request.Path,
                appId,
                platform,
                currentVersion);

            await WriteInvalidMetadataResponseAsync(context);
            return;
        }

        if (evaluation.ShouldForceUpgrade)
        {
            logger.LogWarning(
                "Blocked Android request from unsupported app version {Version} on {Path} for AppId={AppId}",
                evaluation.CurrentVersion,
                context.Request.Path,
                appId);

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
                "Rejected request with incomplete Android app version metadata on {Path}. AppId={AppId}, Platform={Platform}, Version={Version}, DeviceModel={DeviceModel}",
                context.Request.Path,
                appId,
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
               || path.StartsWith("/api/hubs/notifications", StringComparison.Ordinal)
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