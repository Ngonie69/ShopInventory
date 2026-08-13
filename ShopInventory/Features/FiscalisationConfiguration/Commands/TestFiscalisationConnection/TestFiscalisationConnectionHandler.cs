using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalisationConfiguration.Commands.TestFiscalisationConnection;

public sealed class TestFiscalisationConnectionHandler(
    IFiscalisationApiClient fiscalisationClient,
    IOptionsMonitor<FiscalisationSettings> fiscalisationSettings,
    ILogger<TestFiscalisationConnectionHandler> logger
) : IRequestHandler<TestFiscalisationConnectionCommand, ErrorOr<TestFiscalisationConnectionResult>>
{
    public async Task<ErrorOr<TestFiscalisationConnectionResult>> Handle(
        TestFiscalisationConnectionCommand command,
        CancellationToken cancellationToken)
    {
        var settings = fiscalisationSettings.CurrentValue;
        var request = command.Request;

        // Blank means "test what the API is running with", which is a different and equally useful
        // question from "test what I have just typed".
        var apiKey = string.IsNullOrWhiteSpace(request?.ApiKey) ? null : request.ApiKey.Trim();
        var deviceId = request?.DeviceId ?? settings.DefaultDeviceId;

        if (apiKey is null && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return new TestFiscalisationConnectionResult(
                false,
                "No API key to test — enter one above, or set Fiscalisation__ApiKey.");
        }

        var probe = await FiscalisationApiKeyProbe.RunAsync(
            fiscalisationClient, apiKey, deviceId, logger, cancellationToken);

        // Inconclusive is reported as a failure here on purpose: this button answers "can I use this
        // right now", and an unreachable platform is a no. Saving treats it differently.
        return new TestFiscalisationConnectionResult(probe.IsAccepted, probe.Message);
    }
}
