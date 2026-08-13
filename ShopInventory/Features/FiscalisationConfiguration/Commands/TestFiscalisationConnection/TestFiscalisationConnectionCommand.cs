using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FiscalisationConfiguration.Commands.TestFiscalisationConnection;

public sealed record TestFiscalisationConnectionResult(
    bool Connected,
    string Message
);

public sealed record TestFiscalisationConnectionCommand(
    TestFiscalisationConnectionRequest? Request
) : IRequest<ErrorOr<TestFiscalisationConnectionResult>>;
