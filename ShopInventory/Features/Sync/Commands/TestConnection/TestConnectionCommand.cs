using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Sync.Commands.TestConnection;

public sealed record TestConnectionCommand() : IRequest<ErrorOr<TestConnectionResult>>;

public sealed record TestConnectionResult(bool IsConnected, string Message);
