using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
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
            // Every value here comes off the request, and a refused request is by definition one
            // somebody may be probing with. Newlines in a header would otherwise let a caller write
            // whole lines of their own into the log somebody opens when handsets stop working —
            // which is exactly when a forged entry would do the most damage. Sanitising replaces the
            // control characters rather than dropping the value, so what was sent is still visible.
            //
            // Path.Value, not Path: logging the PathString itself renders ToUriComponent(), which
            // percent-escapes the line breaks and so happens to be safe already — but only as a
            // side effect of the struct, and it escapes the whole path along with them. Handing the
            // sanitiser the raw value makes the guard the thing that neutralises the newline, so it
            // cannot be dropped without a test noticing, and leaves an ordinary path readable.
            logger.LogWarning(
                "Rejected Android request with invalid app version metadata on {Path}. AppId={AppId}, Platform={Platform}, Version={Version}",
                SensitiveDataSanitizer.SanitizeIdentifierForLog(context.Request.Path.Value),
                SensitiveDataSanitizer.SanitizeIdentifierForLog(appId),
                SensitiveDataSanitizer.SanitizeIdentifierForLog(platform),
                SensitiveDataSanitizer.SanitizeIdentifierForLog(currentVersion));

            await WriteInvalidMetadataResponseAsync(context);
            return;
        }

        if (evaluation.ShouldForceUpgrade)
        {
            // CurrentVersion is the caller's header trimmed, not a catalogue value: an interior
            // newline survives Trim() untouched.
            logger.LogWarning(
                "Blocked Android request from unsupported app version {Version} on {Path} for AppId={AppId}",
                SensitiveDataSanitizer.SanitizeIdentifierForLog(evaluation.CurrentVersion),
                SensitiveDataSanitizer.SanitizeIdentifierForLog(context.Request.Path.Value),
                SensitiveDataSanitizer.SanitizeIdentifierForLog(appId));

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
                SensitiveDataSanitizer.SanitizeIdentifierForLog(context.Request.Path.Value),
                SensitiveDataSanitizer.SanitizeIdentifierForLog(appId),
                SensitiveDataSanitizer.SanitizeIdentifierForLog(platform),
                SensitiveDataSanitizer.SanitizeIdentifierForLog(currentVersion),
                SensitiveDataSanitizer.SanitizeIdentifierForLog(deviceModel));

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