using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// <c>sap.connection.lost</c> and <c>sap.connection.restored</c> fire on the SAP check's own edges —
/// once per change, never while it holds.
/// </summary>
/// <remarks>
/// These drive <see cref="SystemFailureAlertJob.EvaluateSapTransition"/>, the rule the job applies,
/// replaying polls with the remembered state carried between them as the Quartz job data map carries
/// it. The SAP check has its own remembered state rather than riding the job's overall status on
/// purpose: the overall status is the worst of every check, so with the database already unhealthy,
/// SAP going down would not change it and no edge would ever appear. The last test pins that.
/// </remarks>
public sealed class SapConnectionWebhookTransitionTests
{
    [Fact]
    public void Going_down_publishes_lost_once_however_long_it_stays_down()
    {
        var run = new SapPolls();

        run.Poll(HealthStatus.Healthy);
        run.Poll(HealthStatus.Unhealthy);
        run.Poll(HealthStatus.Unhealthy);
        run.Poll(HealthStatus.Unhealthy);

        Assert.Equal([WebhookEventTypes.SapConnectionLost], run.Published);
    }

    [Fact]
    public void Coming_back_publishes_restored_once()
    {
        var run = new SapPolls();

        run.Poll(HealthStatus.Unhealthy);
        run.Poll(HealthStatus.Healthy);
        run.Poll(HealthStatus.Healthy);

        Assert.Equal(
            [WebhookEventTypes.SapConnectionLost, WebhookEventTypes.SapConnectionRestored],
            run.Published);
    }

    [Fact]
    public void A_healthy_sap_on_the_first_poll_is_not_news()
    {
        var run = new SapPolls();

        run.Poll(HealthStatus.Healthy);
        run.Poll(HealthStatus.Healthy);

        Assert.Empty(run.Published);
    }

    [Fact]
    public void A_sap_already_down_on_the_first_poll_is_reported()
    {
        // Nothing remembered reads as up, so this is a change and the true thing to say.
        var run = new SapPolls();

        run.Poll(HealthStatus.Unhealthy);

        Assert.Equal([WebhookEventTypes.SapConnectionLost], run.Published);
    }

    [Fact]
    public void Lost_is_published_whatever_the_rest_of_the_system_is_doing()
    {
        // The rule sees only the SAP entry. This is the case the overall status could not serve: an
        // already-unhealthy database leaves the worst case unchanged when SAP drops, so an edge on
        // the overall status would never have fired.
        var (eventType, lastPublished) = SystemFailureAlertJob.EvaluateSapTransition(
            HealthStatus.Unhealthy,
            lastPublished: "up");

        Assert.Equal(WebhookEventTypes.SapConnectionLost, eventType);
        Assert.Equal("down", lastPublished);
    }

    /// <summary>Carries the remembered state between polls as the job data map does.</summary>
    private sealed class SapPolls
    {
        private string? _lastPublished;

        public List<string> Published { get; } = [];

        public void Poll(HealthStatus sapStatus)
        {
            var (eventType, lastPublished) = SystemFailureAlertJob.EvaluateSapTransition(sapStatus, _lastPublished);
            _lastPublished = lastPublished;

            if (eventType is not null)
            {
                Published.Add(eventType);
            }
        }
    }
}
