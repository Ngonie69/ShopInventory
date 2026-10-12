using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the Exception Center keeps working past the life of the access token it was opened
/// with.
/// </summary>
/// <remarks>
/// The page refreshes itself on a timer and is kept open through a shift. Its service answers a
/// failed read with null and a failed action with false, so a 401 on every call would show an
/// operator a dashboard that would not load and a Retry that "failed" on a sale that was never
/// asked about.
/// </remarks>
public sealed class ExceptionCenterServiceTokenRenewalTests
{
    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_dashboard_is_read_again()
    {
        var session = ExpiringSession.RejectedByTheApi(_ => """{"items":[{"source":"invoice-queue"}]}""");

        var dashboard = await CreateService(session).GetDashboardAsync();

        Assert.Single(dashboard!.Items);
        Assert.Equal(
            [
                $"GET /api/exception-center {ExpiringSession.StaleToken}",
                ExpiringSession.RefreshOnStaleToken,
                $"GET /api/exception-center {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_retry_is_sent_again()
    {
        var session = ExpiringSession.RejectedByTheApi();

        var retried = await CreateService(session).RetryItemAsync("invoice-queue", "42");

        Assert.True(retried);
        Assert.Equal(
            [
                $"POST /api/exception-center/items/invoice-queue/42/retry {ExpiringSession.StaleToken}",
                ExpiringSession.RefreshOnStaleToken,
                $"POST /api/exception-center/items/invoice-queue/42/retry {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    [Fact]
    public async Task A_token_past_its_expiry_is_renewed_before_the_batch_is_retried()
    {
        var session = ExpiringSession.PastItsExpiry(_ => """{"requestedCount":1,"retriedCount":1}""");

        var result = await CreateService(session).RetryBatchAsync(
            [new ExceptionCenterItemRefModel { Source = "invoice-queue", ItemKey = "42" }]);

        Assert.Equal(1, result?.RetriedCount);
        Assert.Equal(
            [
                ExpiringSession.RefreshBeforeSending,
                $"POST /api/exception-center/items/retry-batch {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    private static ExceptionCenterService CreateService(ExpiringSession session) =>
        new(
            session.HttpClient,
            NullLogger<ExceptionCenterService>.Instance,
            session.LocalStorage,
            session.AuthStateProvider);
}
