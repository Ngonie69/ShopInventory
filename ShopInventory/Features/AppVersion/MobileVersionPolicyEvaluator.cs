using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;

namespace ShopInventory.Features.AppVersion;

public sealed class MobileVersionPolicyEvaluator(
    IOptionsMonitor<MobileVersionPolicyOptions> optionsMonitor,
    ILogger<MobileVersionPolicyEvaluator> logger
) : IMobileVersionPolicyEvaluator
{
    private const string AndroidPlatform = "android";
    private const string OkStatus = "ok";
    private const string WarnStatus = "warn";
    private const string BlockStatus = "block";

    public MobileVersionPolicyEvaluation Evaluate(string? platform, string? currentVersion)
    {
        var checkedAtUtc = DateTime.UtcNow;
        var options = optionsMonitor.CurrentValue;
        var normalizedPlatform = Normalize(platform);
        var normalizedCurrentVersion = Normalize(currentVersion);
        var targetsAndroid = string.Equals(normalizedPlatform, AndroidPlatform, StringComparison.OrdinalIgnoreCase);

        if (!options.Enabled || !targetsAndroid)
        {
            return new MobileVersionPolicyEvaluation(
                OkStatus,
                normalizedCurrentVersion,
                Normalize(options.LatestVersion) ?? string.Empty,
                Normalize(options.RecommendedVersion) ?? string.Empty,
                Normalize(options.MinimumSupportedVersion) ?? string.Empty,
                Normalize(options.GooglePlayUrl) ?? string.Empty,
                Normalize(options.ReleaseNotes),
                null,
                false,
                checkedAtUtc,
                false,
                TryParseVersion(normalizedCurrentVersion, out _),
                options.RequireHeaders);
        }

        if (!TryParseVersion(options.LatestVersion, out var latestVersion) || latestVersion is null)
        {
            logger.LogWarning("Mobile version policy is enabled but LatestVersion is invalid: {LatestVersion}", options.LatestVersion);

            return new MobileVersionPolicyEvaluation(
                OkStatus,
                normalizedCurrentVersion,
                Normalize(options.LatestVersion) ?? string.Empty,
                Normalize(options.RecommendedVersion) ?? string.Empty,
                Normalize(options.MinimumSupportedVersion) ?? string.Empty,
                Normalize(options.GooglePlayUrl) ?? string.Empty,
                Normalize(options.ReleaseNotes),
                null,
                false,
                checkedAtUtc,
                false,
                TryParseVersion(normalizedCurrentVersion, out _),
                options.RequireHeaders);
        }

        var configuredLatestVersion = latestVersion;

        var recommendedVersion = ResolveConfiguredVersion(
            options.RecommendedVersion,
            configuredLatestVersion,
            nameof(options.RecommendedVersion),
            upperBound: configuredLatestVersion);

        var minimumSupportedVersion = ResolveConfiguredVersion(
            options.MinimumSupportedVersion,
            recommendedVersion,
            nameof(options.MinimumSupportedVersion),
            upperBound: recommendedVersion);

        var hasValidClientVersion = TryParseVersion(normalizedCurrentVersion, out var clientVersion);
        var status = OkStatus;
        var shouldForceUpgrade = false;
        string? message = null;

        if (hasValidClientVersion)
        {
            if (clientVersion!.CompareTo(minimumSupportedVersion) < 0)
            {
                status = BlockStatus;
                shouldForceUpgrade = true;
                message = string.IsNullOrWhiteSpace(options.BlockMessage)
                    ? Errors.Auth.AppVersionBlocked.Description
                    : options.BlockMessage.Trim();
            }
            else if (clientVersion.CompareTo(recommendedVersion) < 0)
            {
                status = WarnStatus;
                message = string.IsNullOrWhiteSpace(options.WarnMessage)
                    ? "A newer Android build is available. Update soon for the best experience."
                    : options.WarnMessage.Trim();
            }
        }

        return new MobileVersionPolicyEvaluation(
            status,
            normalizedCurrentVersion,
            configuredLatestVersion.ToString(),
            recommendedVersion.ToString(),
            minimumSupportedVersion.ToString(),
            Normalize(options.GooglePlayUrl) ?? string.Empty,
            Normalize(options.ReleaseNotes),
            message,
            shouldForceUpgrade,
            checkedAtUtc,
            true,
            hasValidClientVersion,
            options.RequireHeaders);
    }

    private Version ResolveConfiguredVersion(string? configuredValue, Version fallback, string propertyName, Version upperBound)
    {
        if (!TryParseVersion(configuredValue, out var parsedVersion))
        {
            if (!string.IsNullOrWhiteSpace(configuredValue))
            {
                logger.LogWarning(
                    "Mobile version policy {PropertyName} is invalid: {ConfiguredValue}. Falling back to {FallbackVersion}",
                    propertyName,
                    configuredValue,
                    fallback);
            }

            return fallback;
        }

        if (parsedVersion!.CompareTo(upperBound) > 0)
        {
            logger.LogWarning(
                "Mobile version policy {PropertyName} exceeds its upper bound. Using {UpperBound} instead of {ConfiguredValue}",
                propertyName,
                upperBound,
                configuredValue);

            return upperBound;
        }

        return parsedVersion;
    }

    private static string? Normalize(string? value) => MobileVersionPolicyConfigurationValue.Normalize(value);

    private static bool TryParseVersion(string? value, out Version? version)
    {
        version = null;

        var candidate = Normalize(value);
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        return Version.TryParse(candidate, out version);
    }
}