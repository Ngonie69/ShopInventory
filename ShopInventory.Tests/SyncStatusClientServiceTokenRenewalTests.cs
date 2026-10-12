using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the Sync Status page keeps working as the signed-in user past the life of the access
/// token it was opened with.
/// </summary>
/// <remarks>
/// The page refreshes itself on a timer and is left open to watch the offline queue drain. The sync
/// routes accept the Web's API key as well as a user's token, so an expired token was never
/// refused: the queue was read, retried and cancelled as the key, by whoever happened to have the
/// page open.
/// </remarks>
public sealed class SyncStatusClientServiceTokenRenewalTests
{
    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_queue_is_read_again()
    {
        var session = ExpiringSession.RejectedByTheApi(_ => """{"pendingCount":4,"failedCount":1}""");

        var queue = await CreateService(session).GetOfflineQueueStatusAsync();

        Assert.Equal(4, queue?.PendingCount);
        Assert.Equal(
            [
                $"GET /api/sync/queue/status {ExpiringSession.StaleToken}",
                ExpiringSession.RefreshOnStaleToken,
                $"GET /api/sync/queue/status {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_retry_is_sent_again()
    {
        var session = ExpiringSession.RejectedByTheApi();

        var retried = await CreateService(session).RetryQueueItemAsync(12);

        Assert.True(retried);
        Assert.Equal(
            [
                $"POST /api/sync/queue/12/retry {ExpiringSession.StaleToken}",
                ExpiringSession.RefreshOnStaleToken,
                $"POST /api/sync/queue/12/retry {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    [Fact]
    public async Task A_token_past_its_expiry_is_renewed_before_the_connection_is_read()
    {
        var session = ExpiringSession.PastItsExpiry(_ => """{"isConnected":true}""");

        var connection = await CreateService(session).GetSapConnectionStatusAsync();

        Assert.NotNull(connection);
        Assert.Equal(
            [
                ExpiringSession.RefreshBeforeSending,
                $"GET /api/sync/sap-connection {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    private static SyncStatusClientService CreateService(ExpiringSession session) =>
        new(
            session.HttpClient,
            // The cache counts come from the Web's own database; none of these calls read it.
            StubProxy.Unused<IDbContextFactory<WebAppDbContext>>(),
            NullLogger<SyncStatusClientService>.Instance,
            session.LocalStorage,
            session.AuthStateProvider);
}
