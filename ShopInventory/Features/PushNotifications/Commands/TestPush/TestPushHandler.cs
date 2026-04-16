using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.PushNotifications.Commands.TestPush;

public sealed class TestPushHandler(
    IPushNotificationService pushService,
    ILogger<TestPushHandler> logger
) : IRequestHandler<TestPushCommand, ErrorOr<TestPushResult>>
{
    public async Task<ErrorOr<TestPushResult>> Handle(
        TestPushCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var sent = await pushService.SendToUserAsync(
                command.UserId,
                "Test Notification",
                "This is a test push notification from ShopInventory.",
                new Dictionary<string, string> { ["type"] = "test" },
                cancellationToken);

            return new TestPushResult(sent, "Test push notification sent");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending test push for user {UserId}", command.UserId);
            return Errors.PushNotification.SendFailed(ex.Message);
        }
    }
}
