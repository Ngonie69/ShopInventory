using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Commands.TestConnection;

public sealed class TestConnectionHandler(
    ISyncStatusService syncStatusService
) : IRequestHandler<TestConnectionCommand, ErrorOr<TestConnectionResult>>
{
    public async Task<ErrorOr<TestConnectionResult>> Handle(
        TestConnectionCommand command,
        CancellationToken cancellationToken)
    {
        var status = await syncStatusService.CheckSapConnectionAsync(cancellationToken);
        return new TestConnectionResult(
            status.IsConnected,
            status.IsConnected ? "Connection successful" : (status.LastError ?? "Connection failed"));
    }
}
