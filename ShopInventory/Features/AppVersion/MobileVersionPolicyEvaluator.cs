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

    public MobileVersionPolicyEvaluation Evaluate(string? appId, string? platform, string? currentVersion)
    {
        var checkedAtUtc = DateTime.UtcNow;
        var options = optionsMonitor.CurrentValue;
        var policy = ResolvePolicy(options, appId);
        var normalizedPlatform = Normalize(platform);
        var normalizedCurrentVersion = Normalize(currentVersion);
        var targetsAndroid = string.Equals(normalizedPlatform, AndroidPlatform, StringComparison.OrdinalIgnoreCase);

        if (!policy.Enabled || !targetsAndroid)
        {
            return new MobileVersionPolicyEvaluation(
                OkStatus,
                normalizedCurrentVersion,
                Normalize(policy.LatestVersion) ?? string.Empty,
                Normalize(policy.RecommendedVersion) ?? string.Empty,
                Normalize(policy.MinimumSupportedVersion) ?? string.Empty,
                Normalize(policy.GooglePlayUrl) ?? string.Empty,
                Normalize(policy.ReleaseNotes),
                null,
                false,
                checkedAtUtc,
                false,
                TryParseVersion(normalizedCurrentVersion, out _),
                policy.RequireHeaders);
        }

        if (!TryParseVersion(policy.LatestVersion, out var latestVersion) || latestVersion is null)
        {
            logger.LogWarning("Mobile version policy is enabled but LatestVersion is invalid: {LatestVersion}", policy.LatestVersion);

            return new MobileVersionPolicyEvaluation(
                OkStatus,
                normalizedCurrentVersion,
                Normalize(policy.LatestVersion) ?? string.Empty,
                Normalize(policy.RecommendedVersion) ?? string.Empty,
                Normalize(policy.MinimumSupportedVersion) ?? string.Empty,
                Normalize(policy.GooglePlayUrl) ?? string.Empty,
                Normalize(policy.ReleaseNotes),
                null,
                false,
                checkedAtUtc,
                false,
                TryParseVersion(normalizedCurrentVersion, out _),
                policy.RequireHeaders);
        }

        var configuredLatestVersion = latestVersion;

        var recommendedVersion = ResolveConfiguredVersion(
            policy.RecommendedVersion,
            configuredLatestVersion,
            nameof(policy.RecommendedVersion),
            upperBound: configuredLatestVersion);

        var minimumSupportedVersion = ResolveConfiguredVersion(
            policy.MinimumSupportedVersion,
            recommendedVersion,
            nameof(policy.MinimumSupportedVersion),
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
                message = string.IsNullOrWhiteSpace(policy.BlockMessage)
                    ? Errors.Auth.AppVersionBlocked.Description
                    : policy.BlockMessage.Trim();
            }
            else if (clientVersion.CompareTo(recommendedVersion) < 0)
            {
                status = WarnStatus;
                message = string.IsNullOrWhiteSpace(policy.WarnMessage)
                    ? "A newer Android build is available. Update soon for the best experience."
                    : policy.WarnMessage.Trim();
            }
        }

        return new MobileVersionPolicyEvaluation(
            status,
            normalizedCurrentVersion,
            configuredLatestVersion.ToString(),
            recommendedVersion.ToString(),
            minimumSupportedVersion.ToString(),
            Normalize(policy.GooglePlayUrl) ?? string.Empty,
            Normalize(policy.ReleaseNotes),
            message,
            shouldForceUpgrade,
            checkedAtUtc,
            true,
            hasValidClientVersion,
            policy.RequireHeaders);
    }

    private static MobileVersionPolicyProfileOptions ResolvePolicy(MobileVersionPolicyOptions options, string? appId)
    {
        if (MobileVersionPolicyAppCatalog.TryResolvePolicyKey(appId, out var policyKey) &&
            options.Apps.TryGetValue(policyKey, out var profile))
        {
            return profile;
        }

        return new MobileVersionPolicyProfileOptions
        {
            Enabled = options.Enabled,
            RequireHeaders = options.RequireHeaders,
            LatestVersion = options.LatestVersion,
            RecommendedVersion = options.RecommendedVersion,
            MinimumSupportedVersion = options.MinimumSupportedVersion,
            GooglePlayUrl = options.GooglePlayUrl,
            ReleaseNotes = options.ReleaseNotes,
            WarnMessage = options.WarnMessage,
            BlockMessage = options.BlockMessage
        };
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

        candidate = ExtractVersionCandidate(candidate);
        return Version.TryParse(candidate, out version);
    }

    private static string ExtractVersionCandidate(string value)
    {
        var startIndex = -1;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsDigit(value[index]))
            {
                startIndex = index;
                break;
            }
        }

        if (startIndex < 0)
            return value;

        var endIndex = startIndex;
        while (endIndex < value.Length && (char.IsDigit(value[endIndex]) || value[endIndex] == '.'))
        {
            endIndex++;
        }

        return value[startIndex..endIndex];
    }
}