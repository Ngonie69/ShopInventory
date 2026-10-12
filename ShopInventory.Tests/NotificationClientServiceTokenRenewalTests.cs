using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the notification bell keeps reading as the signed-in user past the life of the access
/// token the page was opened with.
/// </summary>
/// <remarks>
/// The bell sits in the layout: it reloads on every hub push and polls when the hub is down, on
/// whatever page is open. The notification routes accept the Web's API key as well as a user's
/// token, so an expired token was never refused. The request was answered for the key instead: the
/// API reads the bell's audience off the caller, and for a key alone that is the key's name and its
/// Admin and ApiUser roles. Whoever was signed in was shown that audience's bell, with no error
/// anywhere.
/// </remarks>
public sealed class NotificationClientServiceTokenRenewalTests
{
    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_bell_is_read_again()
    {
        var session = ExpiringSession.RejectedByTheApi(_ => """{"totalCount":2,"unreadCount":2,"notifications":[]}""");

        var notifications = await CreateService(session).GetNotificationsAsync(1, 20, false);

        Assert.Equal(2, notifications?.UnreadCount);
        Assert.Equal(
            [
                $"GET /api/notification {ExpiringSession.StaleToken}",
                ExpiringSession.RefreshOnStaleToken,
                $"GET /api/notification {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    [Fact]
    public async Task A_token_past_its_expiry_is_renewed_before_the_bell_is_read()
    {
        var session = ExpiringSession.PastItsExpiry(_ => """{"unreadCount":5}""");

        var unread = await CreateService(session).GetUnreadCountAsync();

        Assert.Equal(5, unread);
        Assert.Equal(
            [
                ExpiringSession.RefreshBeforeSending,
                $"GET /api/notification/unread-count {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_marking_as_read_is_sent_again()
    {
        var session = ExpiringSession.RejectedByTheApi();

        var marked = await CreateService(session).MarkAsReadAsync([7, 8]);

        Assert.True(marked);
        Assert.Equal(
            [
                $"POST /api/notification/mark-read {ExpiringSession.StaleToken}",
                ExpiringSession.RefreshOnStaleToken,
                $"POST /api/notification/mark-read {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    private static NotificationClientService CreateService(ExpiringSession session) =>
        new(
            session.HttpClient,
            NullLogger<NotificationClientService>.Instance,
            session.LocalStorage,
            session.AuthStateProvider);
}
