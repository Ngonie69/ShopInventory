using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.AppVersion;

public sealed class MobileOrderStatusCompatibilityService(
    IHttpContextAccessor httpContextAccessor,
    IOptionsMonitor<MobileVersionPolicyOptions> optionsMonitor,
    ILogger<MobileOrderStatusCompatibilityService> logger)
{
    private const string PlatformHeaderName = "X-App-Platform";
    private const string VersionHeaderName = "X-App-Version";
    private const string AndroidPlatform = "android";

    public bool ShouldUseLegacyDraftStatus()
    {
        var headers = httpContextAccessor.HttpContext?.Request.Headers;
        if (headers is null)
            return false;

        var platform = Normalize(headers[PlatformHeaderName].FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(platform)
            && !string.Equals(platform, AndroidPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var currentVersion = Normalize(headers[VersionHeaderName].FirstOrDefault());
        var pendingAwareVersion = ResolvePendingAwareVersion();
        return !Version.TryParse(currentVersion, out var parsedVersion)
            || parsedVersion.CompareTo(pendingAwareVersion) < 0;
    }

    public SalesOrderStatus GetVisibleMobileStatus(SalesOrderStatus status, bool isSynced, int? sapDocNum)
    {
        var normalizedStatus = GetPendingAwareStatus(status, isSynced, sapDocNum);
        return ShouldUseLegacyDraftStatus() && normalizedStatus == SalesOrderStatus.Pending
            ? SalesOrderStatus.Draft
            : normalizedStatus;
    }

    public static SalesOrderStatus GetPendingAwareStatus(SalesOrderStatus status, bool isSynced, int? sapDocNum)
    {
        if (status == SalesOrderStatus.Approved && sapDocNum.GetValueOrDefault() <= 0)
            return SalesOrderStatus.Pending;

        if (status == SalesOrderStatus.Draft && (!isSynced || sapDocNum.GetValueOrDefault() <= 0))
            return SalesOrderStatus.Pending;

        return status;
    }

    private static string? Normalize(string? value)
        => MobileVersionPolicyConfigurationValue.Normalize(value);

    private Version ResolvePendingAwareVersion()
    {
        var configuredValue = Normalize(optionsMonitor.CurrentValue.PendingStatusAwareVersion)
            ?? MobileVersionPolicyOptions.DefaultPendingStatusAwareVersion;

        if (Version.TryParse(configuredValue, out var parsedVersion) && parsedVersion is not null)
            return parsedVersion;

        logger.LogWarning(
            "Mobile pending-status compatibility version is invalid: {ConfiguredValue}. Falling back to {FallbackVersion}",
            optionsMonitor.CurrentValue.PendingStatusAwareVersion,
            MobileVersionPolicyOptions.DefaultPendingStatusAwareVersion);

        return Version.Parse(MobileVersionPolicyOptions.DefaultPendingStatusAwareVersion);
    }
}