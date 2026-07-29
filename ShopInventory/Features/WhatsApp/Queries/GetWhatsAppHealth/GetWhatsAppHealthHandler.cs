using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.WhatsApp.Queries.GetWhatsAppHealth;

public sealed class GetWhatsAppHealthHandler(
    IOpenWAClient openWaClient,
    IOptions<OpenWASettings> settings,
    ILogger<GetWhatsAppHealthHandler> logger) : IRequestHandler<GetWhatsAppHealthQuery, ErrorOr<WhatsAppHealthDto>>
{
    private readonly IOpenWAClient _openWaClient = openWaClient;
    private readonly OpenWASettings _settings = settings.Value;
    private readonly ILogger<GetWhatsAppHealthHandler> _logger = logger;

    public async Task<ErrorOr<WhatsAppHealthDto>> Handle(
        GetWhatsAppHealthQuery query,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return Errors.WhatsApp.Disabled;
        }

        if (string.IsNullOrWhiteSpace(_settings.BaseUrl) || _settings.BaseUrl.StartsWith("${", StringComparison.Ordinal))
        {
            return Errors.WhatsApp.InvalidConfiguration("OpenWA:BaseUrl must be configured to a valid absolute URL.");
        }

        if (_settings.HealthEndpointPaths is null || _settings.HealthEndpointPaths.Length == 0)
        {
            return Errors.WhatsApp.InvalidConfiguration("OpenWA:HealthEndpointPaths must contain at least one candidate path.");
        }

        try
        {
            var result = await _openWaClient.GetHealthAsync(cancellationToken);
            if (result is null)
            {
                return Errors.WhatsApp.Unreachable("No response was returned by the OpenWA host.");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving OpenWA health");
            return Errors.WhatsApp.Unreachable(ex.Message);
        }
    }
}