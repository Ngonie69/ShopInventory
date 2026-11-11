using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the date window and send times of the POD report email schedules.
/// </summary>
public sealed class PodReportEmailScheduleTests
{
    [Theory]
    [InlineData("2026-11-11T08:00:00", "2026-01-01", "2026-11-11")]
    [InlineData("2027-01-01T06:00:00", "2027-01-01", "2027-01-01")]
    [InlineData("2028-12-31T23:59:00", "2028-01-01", "2028-12-31")]
    public void YearToDateRunsFromFirstJanuaryThroughTheSendDate(string nowLocal, string expectedFrom, string expectedTo)
    {
        var (fromDate, toDate) = PodReportEmailService.GetPeriod(
            PodReportEmailFrequency.YearToDateDaily,
            intervalDays: null,
            DateTime.Parse(nowLocal));

        Assert.Equal(DateTime.Parse(expectedFrom), fromDate);
        Assert.Equal(DateTime.Parse(expectedTo), toDate);
    }

    [Fact]
    public void YearToDateIsSentEveryDayAtItsTime()
    {
        var schedule = new PodReportEmailSchedule
        {
            Frequency = nameof(PodReportEmailFrequency.YearToDateDaily),
            SendMinuteOfDay = 8 * 60
        };

        var beforeSendTime = new DateTime(2026, 11, 11, 7, 0, 0);
        var afterSendTime = new DateTime(2026, 11, 11, 9, 0, 0);

        Assert.Equal(
            new DateTime(2026, 11, 11, 8, 0, 0),
            PodReportEmailJob.ComputeNextDueLocal(schedule, PodReportEmailFrequency.YearToDateDaily, beforeSendTime));
        Assert.Equal(
            new DateTime(2026, 11, 12, 8, 0, 0),
            PodReportEmailJob.ComputeNextDueLocal(schedule, PodReportEmailFrequency.YearToDateDaily, afterSendTime));
        Assert.Equal(
            new DateTime(2026, 11, 10, 8, 0, 0),
            PodReportEmailJob.ComputeMostRecentDueLocal(schedule, PodReportEmailFrequency.YearToDateDaily, beforeSendTime));
        Assert.Equal(
            new DateTime(2026, 11, 11, 8, 0, 0),
            PodReportEmailJob.ComputeMostRecentDueLocal(schedule, PodReportEmailFrequency.YearToDateDaily, afterSendTime));
    }

    [Fact]
    public void YearToDateRoundTripsThroughItsStoredName()
    {
        Assert.Equal(
            PodReportEmailFrequency.YearToDateDaily,
            PodReportEmailService.ParseFrequency(nameof(PodReportEmailFrequency.YearToDateDaily)));
    }

    /// <summary>
    /// The label switches fall back to "Weekly", so a frequency missing from one is mislabelled
    /// in every email it sends rather than failing.
    /// </summary>
    [Fact]
    public void EveryFrequencyHasItsOwnEmailLabel()
    {
        var labels = Enum.GetValues<PodReportEmailFrequency>()
            .Select(frequency => PodReportEmailService.GetFrequencyLabel(frequency, intervalDays: 3))
            .ToList();

        Assert.Equal(labels.Count, labels.Distinct().Count());
    }
}
