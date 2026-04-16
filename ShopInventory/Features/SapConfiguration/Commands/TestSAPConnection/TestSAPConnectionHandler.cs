using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.SapConfiguration.Commands.TestSAPConnection;

public sealed class TestSAPConnectionHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<TestSAPConnectionHandler> logger
) : IRequestHandler<TestSAPConnectionCommand, ErrorOr<TestSAPConnectionResult>>
{
    public async Task<ErrorOr<TestSAPConnectionResult>> Handle(
        TestSAPConnectionCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            bool connected;
            var request = command.Request;

            if (request != null &&
                !string.IsNullOrWhiteSpace(request.ServiceLayerUrl) &&
                !string.IsNullOrWhiteSpace(request.CompanyDB) &&
                !string.IsNullOrWhiteSpace(request.UserName) &&
                !string.IsNullOrWhiteSpace(request.Password))
            {
                connected = await sapClient.TestConnectionWithCredentialsAsync(
                    request.ServiceLayerUrl, request.CompanyDB, request.UserName, request.Password,
                    cancellationToken);
            }
            else
            {
                connected = await sapClient.TestConnectionAsync(cancellationToken);
            }

            return new TestSAPConnectionResult(
                connected,
                connected ? "Connection successful" : "Connection failed. Please verify your credentials and Service Layer URL.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SAP connection test failed");
            return new TestSAPConnectionResult(false, $"Connection failed: {ex.Message}");
        }
    }
}
