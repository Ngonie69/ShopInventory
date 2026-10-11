using ShopInventory.Web.Components.Layout;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The words on the staff banner. The first half pins what it said before it could be told why, word
/// for word, because that is what it must still say whenever SL Health has nothing to add.
/// </summary>
public sealed class SapOutageBannerTextTests
{
    // 10:00 UTC is 12:00 in CAT.
    private static readonly DateTime Now = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void With_sap_up_there_is_no_banner()
    {
        Assert.Null(SapOutageBannerText.For(new SapAvailabilityResponse(), Now));
        Assert.Null(SapOutageBannerText.For(null, Now));
    }

    [Fact]
    public void Down_without_an_explanation_it_reads_as_it_always_has()
    {
        var text = SapOutageBannerText.For(
            new SapAvailabilityResponse { IsDown = true, Cause = "Unreachable", SinceUtc = Now.AddMinutes(-30) }, Now);

        Assert.Equal(
            new SapOutageBannerText(
                true,
                "SAP is unavailable since 11:30",
                null,
                "Sales carry on and are kept here, and post to SAP when it returns. Stock transfers and van loads have to wait."),
            text);
    }

    [Fact]
    public void Down_with_sales_waiting_it_counts_them()
    {
        var text = SapOutageBannerText.For(
            new SapAvailabilityResponse { IsDown = true, Cause = "Unreachable", SinceUtc = Now.AddDays(-1), SalesAwaitingSap = 1204 }, Now);

        Assert.Equal(
            new SapOutageBannerText(
                true,
                "SAP is unavailable since Sun 11 Oct 12:00",
                null,
                "Sales carry on and are kept here. 1,204 sales recorded since then will post to SAP when it returns."),
            text);
    }

    [Fact]
    public void Switched_off_it_says_so()
    {
        var text = SapOutageBannerText.For(
            new SapAvailabilityResponse { IsDown = true, Cause = "SwitchedOff", SinceUtc = Now.AddMinutes(-5) }, Now);

        Assert.Equal("The SAP connection is turned off since 11:55", text!.Headline);
        Assert.Null(text.Why);
    }

    [Fact]
    public void Back_it_says_what_is_still_posting()
    {
        var waiting = SapOutageBannerText.For(
            new SapAvailabilityResponse { EndedAtUtc = Now.AddMinutes(-10), SinceUtc = Now.AddHours(-2), SalesAwaitingSap = 1 }, Now);
        var done = SapOutageBannerText.For(
            new SapAvailabilityResponse { EndedAtUtc = Now.AddMinutes(-10), SinceUtc = Now.AddHours(-2) }, Now);

        Assert.Equal(
            new SapOutageBannerText(
                false,
                "SAP is back since 11:50",
                null,
                "1 sale from the outage is still waiting to post. Van sales post at the end of the day as usual."),
            waiting);
        Assert.Equal(
            new SapOutageBannerText(false, "SAP is back since 11:50", null, "Everything recorded during the outage has posted."),
            done);
    }

    [Theory]
    [InlineData("planned maintenance until 03:00 (HANA backup)", "Planned maintenance until 03:00 (HANA backup).")]
    [InlineData("SAP is being restarted", "SAP is being restarted.")]
    [InlineData("login is failing: Server Tools not answering", "Login is failing: Server Tools not answering.")]
    public void Down_with_an_explanation_it_says_why_as_a_sentence_of_its_own(string explanation, string shown)
    {
        var text = SapOutageBannerText.For(
            new SapAvailabilityResponse { IsDown = true, Cause = "Unreachable", SinceUtc = Now.AddMinutes(-30), Explanation = explanation },
            Now);

        Assert.Equal(
            new SapOutageBannerText(
                true,
                "SAP is unavailable since 11:30",
                shown,
                "Sales carry on and are kept here, and post to SAP when it returns. Stock transfers and van loads have to wait."),
            text);
    }

    [Fact]
    public void An_explanation_is_never_shown_on_the_back_notice_or_when_it_is_blank()
    {
        var back = SapOutageBannerText.For(
            new SapAvailabilityResponse { EndedAtUtc = Now.AddMinutes(-10), Explanation = "SAP is being restarted" }, Now);
        var blank = SapOutageBannerText.For(
            new SapAvailabilityResponse { IsDown = true, SinceUtc = Now, Explanation = "   " }, Now);

        Assert.Null(back!.Why);
        Assert.Null(blank!.Why);
    }
}
