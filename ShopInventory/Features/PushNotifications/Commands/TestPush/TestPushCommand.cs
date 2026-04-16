using ErrorOr;
using MediatR;

namespace ShopInventory.Features.PushNotifications.Commands.TestPush;

public sealed record TestPushCommand(
    Guid UserId
) : IRequest<ErrorOr<TestPushResult>>;

public sealed record TestPushResult(
    int Sent,
    string Message
);
