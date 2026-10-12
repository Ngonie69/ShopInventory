using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the desktop integration pages keep working as the signed-in user past the life of the
/// access token they were opened with.
/// </summary>
/// <remarks>
/// Desktop Transactions polls the queue, Desktop Sales and Local Stock reload on every hub push, and
/// all three are left open through a trading day. The desktop integration routes accept the Web's
/// API key as well as a user's token, so an expired token was never refused: a retry, a cancel or a
/// credit note went through as the key, with the key's roles and the key's name in the audit trail,
/// for whoever had the page open.
/// </remarks>
public sealed class DesktopIntegrationServiceTokenRenewalTests
{
    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_queue_is_read_again()
    {
        var session = ExpiringSession.RejectedByTheApi(_ => """{"pending":3,"requiresReview":1}""");

        var (stats, error) = await CreateService(session).GetQueueStatsAsync();

        Assert.Null(error);
        Assert.Equal(3, stats?.Pending);
        Assert.Equal(
            [
                $"GET /api/DesktopIntegration/queue/stats {ExpiringSession.StaleToken}",
                ExpiringSession.RefreshOnStaleToken,
                $"GET /api/DesktopIntegration/queue/stats {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    [Fact]
    public async Task A_token_past_its_expiry_is_renewed_before_a_queued_invoice_is_retried()
    {
        var session = ExpiringSession.PastItsExpiry();

        var retried = await CreateService(session).RetryQueuedInvoiceAsync("KEF-1001");

        Assert.True(retried);
        Assert.Equal(
            [
                ExpiringSession.RefreshBeforeSending,
                $"POST /api/DesktopIntegration/queue/KEF-1001/retry {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    /// <summary>
    /// The credit note calls build their own request message, which cannot be sent twice: the retry
    /// has to build a second one, body and all.
    /// </summary>
    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_credit_note_request_is_built_again()
    {
        var creditNote = Guid.NewGuid();
        string? bodyTheApiAccepted = null;
        var session = ExpiringSession.RejectedByTheApi(request =>
        {
            bodyTheApiAccepted = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return $$"""{"id":"{{creditNote}}","number":"CN-7","status":"Issued","sapStatus":"Posted"}""";
        });

        var row = await CreateService(session).MarkCreditNoteRaisedAsync(creditNote, sapDocNum: 5521);

        Assert.Equal("CN-7", row.Number);
        Assert.Equal("""{"sapDocNum":5521}""", bodyTheApiAccepted);
        Assert.Equal(
            [
                $"POST /api/DesktopIntegration/credit-notes/{creditNote}/mark-raised {ExpiringSession.StaleToken}",
                ExpiringSession.RefreshOnStaleToken,
                $"POST /api/DesktopIntegration/credit-notes/{creditNote}/mark-raised {ExpiringSession.FreshToken}"
            ],
            session.Calls);
    }

    private static DesktopIntegrationService CreateService(ExpiringSession session) =>
        new(
            session.HttpClient,
            NullLogger<DesktopIntegrationService>.Instance,
            session.LocalStorage,
            session.AuthStateProvider);
}
