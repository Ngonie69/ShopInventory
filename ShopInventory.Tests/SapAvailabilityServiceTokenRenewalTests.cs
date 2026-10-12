using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the SAP outage banner keeps reading past the life of the access token the page was
/// opened with.
/// </summary>
/// <remarks>
/// The banner sits in the layout and polls on every page, so it is asking long after any token it
/// started with has run out. Its service swallows a failed read and leaves the banner as it last
/// was, so a 401 on every poll would not show as an error: the banner would simply stop hearing
/// that SAP had gone down, or that it was back.
/// </remarks>
public sealed class SapAvailabilityServiceTokenRenewalTests
{
    private const string Down = """{"isDown":true,"cause":"Unreachable","salesAwaitingSap":3}""";

    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_read_is_sent_again()
    {
        var session = ExpiringSession.RejectedByTheApi(_ => Down);

        var availability = await CreateService(session).GetAsync();

        Assert.True(availability?.IsDown);
        Assert.Equal(
            [
                $"GET /api/sync/sap-availability {ExpiringSession.StaleToken}",
                ExpiringSession.RefreshOnStaleToken,
                $"GET /api/sync/sap-availability {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    [Fact]
    public async Task A_token_past_its_expiry_is_renewed_before_the_read()
    {
        var session = ExpiringSession.PastItsExpiry(_ => Down);

        var availability = await CreateService(session).GetAsync();

        Assert.Equal(3, availability?.SalesAwaitingSap);
        Assert.Equal(
            [
                ExpiringSession.RefreshBeforeSending,
                $"GET /api/sync/sap-availability {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    private static SapAvailabilityService CreateService(ExpiringSession session) =>
        new(
            session.HttpClient,
            NullLogger<SapAvailabilityService>.Instance,
            session.LocalStorage,
            session.AuthStateProvider);
}
