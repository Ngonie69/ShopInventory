using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The periods and links the van reports share. Every dated report reads the same presets and the
/// same query names, so a slip here is a slip on all of them at once.
/// </summary>
public class VanReportPeriodTests
{
    // A Saturday, late in the month.
    private static readonly DateTime Today = new(2026, 10, 24);

    [Fact]
    public void The_standard_presets_end_today_and_start_where_their_names_say()
    {
        var options = VanReportPeriod.Options(Today).ToDictionary(option => option.Label);

        Assert.Equal((Today, Today), (options["Today"].From, options["Today"].To));
        Assert.Equal((Today.AddDays(-1), Today.AddDays(-1)), (options["Yesterday"].From, options["Yesterday"].To));
        // The week starts on Monday.
        Assert.Equal(new DateTime(2026, 10, 19), options["This week"].From);
        Assert.Equal(new DateTime(2026, 10, 1), options["This month"].From);
        Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)), (options["Last month"].From, options["Last month"].To));
    }

    [Fact]
    public void A_pages_own_default_is_a_preset_of_its_own()
    {
        var labels = VanReportPeriod.Options(Today, [14]).Select(option => option.Label).ToList();
        var fourteen = VanReportPeriod.Options(Today, [14]).Single(option => option.Label == "Last 14 days");

        Assert.Equal(["Today", "Yesterday", "This week", "Last 14 days", "This month", "Last month"], labels);
        Assert.Equal(new DateTime(2026, 10, 11), fourteen.From);
    }

    /// <summary>
    /// The Daily Truck Report's month is the calendar month: its target is built on all of the month's
    /// working days, and a period that stops today cannot say how far behind the month a truck is.
    /// </summary>
    [Fact]
    public void A_whole_month_report_takes_this_month_to_its_last_day()
    {
        var month = VanReportPeriod.Options(Today, wholeMonths: true).Single(option => option.Label == "This month");
        var soFar = VanReportPeriod.Options(Today).Single(option => option.Label == "This month");

        Assert.Equal((new DateTime(2026, 10, 1), new DateTime(2026, 10, 31)), (month.From, month.To));
        Assert.Equal((new DateTime(2026, 10, 1), Today), (soFar.From, soFar.To));
    }

    [Fact]
    public void On_a_monday_this_week_is_today()
    {
        var monday = new DateTime(2026, 10, 19);

        var week = VanReportPeriod.Options(monday).Single(option => option.Label == "This week");

        Assert.Equal((monday, monday), (week.From, week.To));
    }

    [Theory]
    [InlineData("2026-10-24", "2026-10-24", "Sat 24 Oct 2026")]
    [InlineData("2026-10-01", "2026-10-24", "1–24 Oct 2026")]
    [InlineData("2026-09-28", "2026-10-24", "28 Sep – 24 Oct 2026")]
    [InlineData("2025-12-29", "2026-01-04", "29 Dec 2025 – 4 Jan 2026")]
    public void A_period_is_described_as_briefly_as_it_can_be(string from, string to, string expected)
    {
        Assert.Equal(expected, VanReportPeriod.Describe(DateTime.Parse(from), DateTime.Parse(to)));
    }

    [Theory]
    [InlineData("2026-10-24", true)]
    [InlineData("24.10.2026", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_query_format_is_read_back_from_a_url(string? value, bool reads)
    {
        Assert.Equal(reads, VanReportPeriod.ParseDay(value).HasValue);
    }

    [Fact]
    public void A_link_carries_the_period_and_only_the_filters_it_names()
    {
        var rep = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal(
            "/van-sales/compliance?from=2026-10-01&to=2026-10-24&route=GUR%2001&rep=11111111222233334444555555555555",
            VanReportLinks.Compliance(new DateTime(2026, 10, 1), Today, rep, "GUR 01"));
        Assert.Equal(
            "/van-sales/activity?from=2026-10-24&to=2026-10-24",
            VanReportLinks.Activity(Today, Today));
        Assert.Equal(rep, VanReportLinks.ParseRep(VanReportLinks.RepKey(rep)));
    }
}
