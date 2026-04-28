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
        var options = optionsMonitor.CurrentValue;

        ErrorOr<MobileVersionPolicySettingsDto> result = new MobileVersionPolicySettingsDto
        {
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

        return Task.FromResult(result);
    }
}