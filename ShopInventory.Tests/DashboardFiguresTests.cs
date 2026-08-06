using ShopInventory.Web.Common;

namespace ShopInventory.Tests;

/// <summary>
/// The day-over-day comparison every dashboard prints under its counts. It was
/// private to one page until the administrator's and the cashier's dashboards
/// both needed it; these are the cases that used to be untestable.
/// </summary>
public sealed class DashboardFiguresTests
{
    [Fact]
    public void Two_empty_days_report_no_comparison()
    {
        // Rendering "0%" on a Sunday would claim a comparison nobody made.
        var (text, direction) = DashboardFigures.BuildTrend(today: 0, yesterday: 0);

        Assert.Null(text);
        Assert.Equal(0, direction);
    }

    [Fact]
    public void A_first_day_of_trading_reports_an_absolute_delta()
    {
        // Yesterday gives nothing to divide by, so a percentage would be
        // infinite. The count itself is the honest reading.
        var (text, direction) = DashboardFigures.BuildTrend(today: 7, yesterday: 0);

        Assert.Equal("+7", text);
        Assert.Equal(1, direction);
    }

    [Theory]
    [InlineData(150, 100, "50%", 1)]
    [InlineData(50, 100, "50%", -1)]
    [InlineData(100, 100, "0%", 0)]
    public void A_normal_day_reports_the_percentage_change(
        int today, int yesterday, string expectedText, int expectedDirection)
    {
        var (text, direction) = DashboardFigures.BuildTrend(today, yesterday);

        // The text is unsigned — direction carries the sign, and the card draws
        // it as an arrow so it survives a monochrome print.
        Assert.Equal(expectedText, text);
        Assert.Equal(expectedDirection, direction);
    }

    [Fact]
    public void A_day_that_went_to_zero_reports_a_full_fall()
    {
        var (text, direction) = DashboardFigures.BuildTrend(today: 0, yesterday: 12);

        Assert.Equal("100%", text);
        Assert.Equal(-1, direction);
    }

    [Fact]
    public void The_percentage_is_rounded_rather_than_truncated()
    {
        // 2 against 3 is 33.33% down; truncation would report 33% either way,
        // rounding is what makes the two directions symmetrical.
        var down = DashboardFigures.BuildTrend(today: 2, yesterday: 3);
        var up = DashboardFigures.BuildTrend(today: 4, yesterday: 3);

        Assert.Equal("33%", down.Text);
        Assert.Equal(-1, down.Direction);
        Assert.Equal("33%", up.Text);
        Assert.Equal(1, up.Direction);
    }
}
