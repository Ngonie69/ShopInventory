using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SapConfiguration.Queries.GetSAPSettings;

public sealed class GetSAPSettingsHandler(
    IOptionsMonitor<Configuration.SAPSettings> sapSettings
) : IRequestHandler<GetSAPSettingsQuery, ErrorOr<SAPConnectionSettingsDto>>
{
    public Task<ErrorOr<SAPConnectionSettingsDto>> Handle(
        GetSAPSettingsQuery request,
        CancellationToken cancellationToken)
    {
        var settings = sapSettings.CurrentValue;
        var dto = new SAPConnectionSettingsDto
        {
            ServiceLayerUrl = settings.ServiceLayerUrl,
            CompanyDB = settings.CompanyDB,
            UserName = settings.Username,
            IsConfigured = !string.IsNullOrEmpty(settings.ServiceLayerUrl)
                           && !string.IsNullOrEmpty(settings.CompanyDB)
                           && !string.IsNullOrEmpty(settings.Username)
        };

        ErrorOr<SAPConnectionSettingsDto> result = dto;
        return Task.FromResult(result);
    }
}
