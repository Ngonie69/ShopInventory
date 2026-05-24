using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;

namespace ShopInventory.Features.AppVersion.Queries.GetMobileVersionPolicySettings;

public sealed class GetMobileVersionPolicySettingsHandler(
    IOptionsMonitor<MobileVersionPolicyOptions> optionsMonitor
) : IRequestHandler<GetMobileVersionPolicySettingsQuery, ErrorOr<MobileVersionPolicySettingsDto>>
{
    public Task<ErrorOr<MobileVersionPolicySettingsDto>> Handle(
        GetMobileVersionPolicySettingsQuery request,
        CancellationToken cancellationToken)
    {
        if (!MobileVersionPolicyAppCatalog.TryResolvePolicyKey(request.AppId, out var policyKey))
        {
            policyKey = MobileVersionPolicyAppCatalog.CheesemanPolicyKey;
        }

        var settings = NormalizeSettings(optionsMonitor.CurrentValue, policyKey);

        ErrorOr<MobileVersionPolicySettingsDto> result = settings;

        return Task.FromResult(result);
    }

    private static MobileVersionPolicySettingsDto NormalizeSettings(MobileVersionPolicyOptions options, string policyKey)
    {
        if (options.Apps.TryGetValue(policyKey, out var profile))
        {
            return NormalizeSettings(profile, policyKey);
        }

        return new MobileVersionPolicySettingsDto
        {
            AppId = policyKey,
            AppDisplayName = MobileVersionPolicyAppCatalog.GetDisplayName(policyKey),
            Enabled = options.Enabled,
            RequireHeaders = options.RequireHeaders,
            LatestVersion = MobileVersionPolicyConfigurationValue.Normalize(options.LatestVersion) ?? string.Empty,
            RecommendedVersion = MobileVersionPolicyConfigurationValue.Normalize(options.RecommendedVersion) ?? string.Empty,
            MinimumSupportedVersion = MobileVersionPolicyConfigurationValue.Normalize(options.MinimumSupportedVersion) ?? string.Empty,
            GooglePlayUrl = MobileVersionPolicyConfigurationValue.Normalize(options.GooglePlayUrl) ?? string.Empty,
            ReleaseNotes = MobileVersionPolicyConfigurationValue.Normalize(options.ReleaseNotes) ?? string.Empty,
            WarnMessage = MobileVersionPolicyConfigurationValue.Normalize(options.WarnMessage) ?? string.Empty,
            BlockMessage = MobileVersionPolicyConfigurationValue.Normalize(options.BlockMessage) ?? string.Empty,
        };
    }

    private static MobileVersionPolicySettingsDto NormalizeSettings(MobileVersionPolicyProfileOptions profile, string policyKey) => new()
    {
        AppId = policyKey,
        AppDisplayName = MobileVersionPolicyAppCatalog.GetDisplayName(policyKey),
        Enabled = profile.Enabled,
        RequireHeaders = profile.RequireHeaders,
        LatestVersion = MobileVersionPolicyConfigurationValue.Normalize(profile.LatestVersion) ?? string.Empty,
        RecommendedVersion = MobileVersionPolicyConfigurationValue.Normalize(profile.RecommendedVersion) ?? string.Empty,
        MinimumSupportedVersion = MobileVersionPolicyConfigurationValue.Normalize(profile.MinimumSupportedVersion) ?? string.Empty,
        GooglePlayUrl = MobileVersionPolicyConfigurationValue.Normalize(profile.GooglePlayUrl) ?? string.Empty,
        ReleaseNotes = MobileVersionPolicyConfigurationValue.Normalize(profile.ReleaseNotes) ?? string.Empty,
        WarnMessage = MobileVersionPolicyConfigurationValue.Normalize(profile.WarnMessage) ?? string.Empty,
        BlockMessage = MobileVersionPolicyConfigurationValue.Normalize(profile.BlockMessage) ?? string.Empty,
    };
}