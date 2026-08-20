using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers how often a push target with no registered devices is mentioned.
/// </summary>
/// <remarks>
/// Every sales order raises a Cashier notification, and on 2026-08-20 every one of them reported
/// reaching nobody — fifteen identical lines for a single standing fact: no device is registered
/// against that role. The condition is worth knowing once; it is not worth a line per order.
/// <para>
/// The counter is deliberately per target. Cashier having nobody says nothing about whether a named
/// driver does, and collapsing them would hide the second behind the first.
/// </para>
/// </remarks>
public sealed class NoDeviceReportThrottleTests
{
    public NoDeviceReportThrottleTests() => ResetReports();

    [Fact]
    public void The_first_time_a_target_reaches_nobody_it_is_reported()
    {
        Assert.True(TryTakeReport("throttle-test-Cashier", out var suppressed));
        Assert.Equal(0, suppressed);
    }

    [Fact]
    public void The_same_target_is_not_reported_again_within_the_hour()
    {
        Assert.True(TryTakeReport("throttle-test-Cashier", out _));

        for (var i = 0; i < 14; i++)
        {
            Assert.False(TryTakeReport("throttle-test-Cashier", out _));
        }
    }

    /// <summary>
    /// Two roles with nobody registered are two facts. One must not silence the other, or a second
    /// misconfiguration would stay invisible behind the first.
    /// </summary>
    [Fact]
    public void A_different_target_is_reported_on_its_own_terms()
    {
        Assert.True(TryTakeReport("throttle-test-Cashier", out _));
        Assert.True(TryTakeReport("throttle-test-PodOperator", out _));
    }

    [Fact]
    public void Target_matching_ignores_case()
    {
        Assert.True(TryTakeReport("throttle-test-Cashier", out _));
        Assert.False(TryTakeReport("throttle-test-cashier", out _));
    }

    /// <summary>
    /// The next report says how big the standing condition is. A line reading "1 in the last hour"
    /// and one reading "15" call for different responses.
    /// </summary>
    [Fact]
    public void The_next_report_counts_what_was_held_back()
    {
        Assert.True(TryTakeReport("throttle-test-Cashier", out _));

        for (var i = 0; i < 14; i++)
        {
            Assert.False(TryTakeReport("throttle-test-Cashier", out _));
        }

        Rewind("throttle-test-Cashier", TimeSpan.FromHours(2));

        Assert.True(TryTakeReport("throttle-test-Cashier", out var suppressed));
        Assert.Equal(14, suppressed);
    }

    [Fact]
    public void The_count_starts_again_after_a_report()
    {
        Assert.True(TryTakeReport("throttle-test-Cashier", out _));
        Assert.False(TryTakeReport("throttle-test-Cashier", out _));

        Rewind("throttle-test-Cashier", TimeSpan.FromHours(2));
        Assert.True(TryTakeReport("throttle-test-Cashier", out var firstSuppressed));
        Assert.Equal(1, firstSuppressed);

        Rewind("throttle-test-Cashier", TimeSpan.FromHours(2));
        Assert.True(TryTakeReport("throttle-test-Cashier", out var secondSuppressed));
        Assert.Equal(0, secondSuppressed);
    }

    private static bool TryTakeReport(string target, out int suppressedSince)
        => NotificationService.TryTakeNoDeviceReport(target, out suppressedSince);

    /// <summary>Backdates a target's last report, standing in for the passage of time.</summary>
    private static void Rewind(string target, TimeSpan by)
    {
        var current = NotificationService.NoDeviceReports[target];
        NotificationService.NoDeviceReports[target] = (current.LastReportedUtc - by, current.SuppressedSince);
    }

    private static void ResetReports() => NotificationService.NoDeviceReports.Clear();
}
