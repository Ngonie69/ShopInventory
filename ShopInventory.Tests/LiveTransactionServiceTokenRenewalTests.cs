using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the live transactions feed keeps reading past the life of the access token the page
/// was opened with.
/// </summary>
/// <remarks>
/// The page exists to be left on a screen: it polls every few seconds and never navigates. Its
/// service answers a failed read with null, which the page shows as "reconnecting", so a 401 on
/// every poll would leave the feed frozen and reconnecting for good an hour after it was opened.
/// </remarks>
public sealed class LiveTransactionServiceTokenRenewalTests
{
    private const string Feed = """{"hasMore":true,"fiscalFeedAvailable":true,"events":[]}""";

    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_read_is_sent_again()
    {
        var session = ExpiringSession.RejectedByTheApi(_ => Feed);

        var feed = await CreateService(session).GetFeedAsync(sinceUtc: null, limit: 50);

        Assert.True(feed?.HasMore);
        Assert.Equal(
            [
                $"GET /api/live-transactions/feed {ExpiringSession.StaleToken}",
                ExpiringSession.RefreshOnStaleToken,
                $"GET /api/live-transactions/feed {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    [Fact]
    public async Task A_token_past_its_expiry_is_renewed_before_the_read()
    {
        var session = ExpiringSession.PastItsExpiry(_ => Feed);

        var feed = await CreateService(session).GetFeedAsync(sinceUtc: DateTime.UtcNow.AddMinutes(-1), limit: 50);

        Assert.True(feed?.FiscalFeedAvailable);
        Assert.Equal(
            [
                ExpiringSession.RefreshBeforeSending,
                $"GET /api/live-transactions/feed {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    private static LiveTransactionService CreateService(ExpiringSession session) =>
        new(
            session.HttpClient,
            NullLogger<LiveTransactionService>.Instance,
            session.LocalStorage,
            session.AuthStateProvider);
}
