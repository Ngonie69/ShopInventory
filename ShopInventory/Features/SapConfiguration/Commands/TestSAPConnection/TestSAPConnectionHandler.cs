using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.SapConfiguration.Commands.TestSAPConnection;

public sealed class TestSAPConnectionHandler(
    ISAPServiceLayerClient sapClient,
    SapConnectionSwitch connectionSwitch,
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
            else if (!connectionSwitch.IsEnabled)
            {
                // The stored credentials are tested through the app's own SAP client, which refuses
                // every request while the connection is off. A test with a password typed in uses a
                // client of its own, so it still works.
                return new TestSAPConnectionResult(
                    false,
                    "The SAP connection is turned off, so the saved credentials cannot be tested. Enter the password to test without turning it on.");
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
