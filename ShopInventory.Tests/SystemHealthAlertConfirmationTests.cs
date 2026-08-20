using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers how long a Degraded reading has to hold before anyone is told about it.
/// </summary>
/// <remarks>
/// On 2026-08-20 SAP wobbled for five minutes — three transient errors and a BadGateway, every one
/// absorbed by the price-list fallback, no user request failed. It produced an email and an org-wide
/// push at 08:11, and a second email and a second push at 08:16 when it cleared. Four messages for
/// something nobody would otherwise have noticed is how recipients learn to ignore the alerts.
/// <para>
/// These drive <see cref="SystemFailureAlertJob.EvaluateConfirmation"/>, which is the rule the job
/// itself applies — the counting is not re-derived here, or the test could agree with itself while
/// the job did something else.
/// </para>
/// </remarks>
public sealed class SystemHealthAlertConfirmationTests
{
    [Fact]
    public void The_2026_08_20_blip_would_not_have_alerted()
    {
        // Two polls five minutes apart: Degraded at 08:11, Healthy by 08:16. The first never earns
        // an alert, so the recovery has nothing to announce either.
        var run = new ConfirmationRun(requiredConfirmations: 2);

        Assert.False(run.Poll(HealthStatus.Degraded, "sap-unstable"));
        Assert.False(run.Poll(HealthStatus.Healthy, fingerprint: string.Empty));

        Assert.Equal(0, run.AlertsSent);
    }

    [Fact]
    public void A_degradation_that_holds_is_alerted_on_the_second_poll()
    {
        var run = new ConfirmationRun(requiredConfirmations: 2);

        Assert.False(run.Poll(HealthStatus.Degraded, "sap-unstable"));
        Assert.True(run.Poll(HealthStatus.Degraded, "sap-unstable"));
    }

    /// <summary>
    /// Consecutive has to mean consecutive. A condition that appears and clears repeatedly must
    /// never accumulate its way to a confirmation it did not earn.
    /// </summary>
    [Fact]
    public void A_recurring_blip_never_accumulates_into_an_alert()
    {
        var run = new ConfirmationRun(requiredConfirmations: 2);

        for (var i = 0; i < 10; i++)
        {
            Assert.False(run.Poll(HealthStatus.Degraded, "sap-unstable"));
            Assert.False(run.Poll(HealthStatus.Healthy, fingerprint: string.Empty));
        }

        Assert.Equal(0, run.AlertsSent);
    }

    /// <summary>A different condition starts its own count rather than inheriting one.</summary>
    [Fact]
    public void A_changed_condition_restarts_the_count()
    {
        var run = new ConfirmationRun(requiredConfirmations: 2);

        Assert.False(run.Poll(HealthStatus.Degraded, "sap-unstable"));
        Assert.False(run.Poll(HealthStatus.Degraded, "postgres-slow"));
        Assert.True(run.Poll(HealthStatus.Degraded, "postgres-slow"));
    }

    [Fact]
    public void An_unhealthy_reading_alerts_on_the_first_poll()
    {
        var run = new ConfirmationRun(requiredConfirmations: 2);

        Assert.True(run.Poll(HealthStatus.Unhealthy, "sap-down"));
    }

    /// <summary>A degradation that escalates must not be held back by the count it had built up.</summary>
    [Fact]
    public void A_degradation_turning_unhealthy_alerts_immediately()
    {
        var run = new ConfirmationRun(requiredConfirmations: 2);

        Assert.False(run.Poll(HealthStatus.Degraded, "sap-unstable"));
        Assert.True(run.Poll(HealthStatus.Unhealthy, "sap-down"));
    }

    [Fact]
    public void Setting_one_confirmation_restores_alerting_on_the_first_sighting()
    {
        var run = new ConfirmationRun(requiredConfirmations: 1);

        Assert.True(run.Poll(HealthStatus.Degraded, "sap-unstable"));
    }

    /// <summary>
    /// Once a condition has been alerted on, repeats belong to the cooldown ladder. Re-applying the
    /// gate would hold back every reminder for a problem already reported.
    /// </summary>
    [Fact]
    public void A_condition_already_alerted_on_does_not_wait_again()
    {
        var (alert, _, _) = SystemFailureAlertJob.EvaluateConfirmation(
            HealthStatus.Degraded,
            conditionChanged: false,
            fingerprint: "sap-unstable",
            pendingFingerprint: string.Empty,
            pendingCount: 0,
            requiredConfirmations: 2);

        Assert.True(alert);
    }

    /// <summary>
    /// Replays polls through the job's own decision, carrying the pending state the way the Quartz
    /// job data map does between fires.
    /// </summary>
    private sealed class ConfirmationRun(int requiredConfirmations)
    {
        private string _pendingFingerprint = string.Empty;
        private int _pendingCount;

        public int AlertsSent { get; private set; }

        public bool Poll(HealthStatus status, string fingerprint)
        {
            if (status == HealthStatus.Healthy)
            {
                // Execute clears the pending state on every healthy poll, alerted or not.
                _pendingFingerprint = string.Empty;
                _pendingCount = 0;
                return false;
            }

            var (alert, pendingFingerprint, pendingCount) = SystemFailureAlertJob.EvaluateConfirmation(
                status,
                conditionChanged: true,
                fingerprint,
                _pendingFingerprint,
                _pendingCount,
                requiredConfirmations);

            _pendingFingerprint = pendingFingerprint;
            _pendingCount = pendingCount;

            if (alert)
            {
                AlertsSent++;
            }

            return alert;
        }
    }
}
