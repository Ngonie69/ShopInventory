using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationSettings;

public sealed class GetFiscalisationSettingsHandler(
    IOptionsMonitor<FiscalisationSettings> fiscalisationSettings
) : IRequestHandler<GetFiscalisationSettingsQuery, ErrorOr<FiscalisationSettingsDto>>
{
    public Task<ErrorOr<FiscalisationSettingsDto>> Handle(
        GetFiscalisationSettingsQuery request,
        CancellationToken cancellationToken)
    {
        // Deliberately what the process is running with, not what web.config now says. A key saved from
        // the settings screen is only live once the app pool has recycled, and the screen has to be able
        // to show which of the two is in force.
        var settings = fiscalisationSettings.CurrentValue;

        var dto = new FiscalisationSettingsDto
        {
            Enabled = settings.Enabled,
            BaseUrl = settings.BaseUrl,
            ApiKeyMasked = FiscalisationApiKeyMask.Mask(settings.ApiKey),
            IsConfigured = !string.IsNullOrWhiteSpace(settings.ApiKey),
            DefaultDeviceId = settings.DefaultDeviceId
        };

        ErrorOr<FiscalisationSettingsDto> result = dto;
        return Task.FromResult(result);
    }
}
