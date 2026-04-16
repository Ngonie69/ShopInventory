using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SapConfiguration.Commands.TestSAPConnection;

public sealed record TestSAPConnectionResult(
    bool Connected,
    string Message
);

public sealed record TestSAPConnectionCommand(
    TestSAPConnectionRequest? Request
) : IRequest<ErrorOr<TestSAPConnectionResult>>;
