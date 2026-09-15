using Quartz;
using ShopInventory.Health;
using static ShopInventory.Health.QuartzWorkersHealthCheck;

namespace ShopInventory.Tests;

/// <summary>
/// The report has to name the trigger that failed. On 2026-09-15 production raised 31 errors saying
/// "daily-incoming-payment trigger is in Error state" while every row the report printed for that job
/// read Normal: the job has a 17:00 cron and a retry interval, the report was keyed by job name, and
/// the healthy cron's row overwrote the broken retry's.
/// </summary>
public sealed class QuartzWorkersHealthCheckTests
{
    private static readonly JobKey DailyPayment = new("daily-incoming-payment");

    [Fact]
    public void A_broken_second_trigger_is_named_and_not_hidden_by_the_healthy_one()
    {
        var (failures, data) = Evaluate(
        [
            // The broken retry first, so a job-keyed report would let the cron overwrite it.
            new TriggerSnapshot(new TriggerKey("daily-incoming-payment-retry"), DailyPayment, TriggerState.Error,
                At("2026-09-15T06:22:06Z"), At("2026-09-15T06:32:06Z")),
            new TriggerSnapshot(new TriggerKey("daily-incoming-payment"), DailyPayment, TriggerState.Normal,
                null, At("2026-09-15T15:00:00Z")),
        ]);

        var failure = Assert.Single(failures);
        Assert.Equal("daily-incoming-payment-retry (job daily-incoming-payment) trigger is in Error state.", failure);

        Assert.Equal(2, data.Count);
        Assert.Contains("state=Error", (string)data["daily-incoming-payment-retry"]);
        Assert.Contains("state=Normal", (string)data["daily-incoming-payment"]);
    }

    [Fact]
    public void A_trigger_named_after_its_job_is_reported_by_that_name_alone()
    {
        var (failures, _) = Evaluate(
        [
            new TriggerSnapshot(new TriggerKey("daily-incoming-payment"), DailyPayment, TriggerState.Error, null, null),
        ]);

        Assert.Equal("daily-incoming-payment trigger is in Error state.", Assert.Single(failures));
    }

    [Theory]
    [InlineData(TriggerState.Normal, false)]
    [InlineData(TriggerState.Blocked, false)]
    [InlineData(TriggerState.Complete, true)]
    public void Only_a_live_trigger_with_a_next_fire_time_passes(TriggerState state, bool hasNoNextFireAndPasses)
    {
        var withNextFire = Evaluate(
            [new TriggerSnapshot(new TriggerKey("t"), new JobKey("j"), state, null, At("2026-09-16T00:00:00Z"))]);
        var withoutNextFire = Evaluate(
            [new TriggerSnapshot(new TriggerKey("t"), new JobKey("j"), state, null, null)]);

        Assert.Empty(withNextFire.Failures);
        Assert.Equal(hasNoNextFireAndPasses, withoutNextFire.Failures.Count == 0);
    }

    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc);
}
