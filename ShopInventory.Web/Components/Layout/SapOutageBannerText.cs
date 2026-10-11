using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Layout;

/// <summary>
/// The words on <see cref="SapOutageBanner"/>, worked out apart from the component so they can be
/// tested without rendering it.
/// </summary>
/// <param name="Down">SAP is unavailable; false for the "SAP is back" notice.</param>
/// <param name="Headline">What has happened and since when.</param>
/// <param name="Why">
/// Why, as SL Health tells it, as a sentence of its own. Null when SL Health is not being read or has
/// nothing to add, and the banner then reads exactly as it did before it could ask.
/// </param>
/// <param name="Detail">What it means for the person reading.</param>
public sealed record SapOutageBannerText(bool Down, string Headline, string? Why, string Detail)
{
    /// <summary>The banner for this answer, or null when there is nothing to show.</summary>
    public static SapOutageBannerText? For(SapAvailabilityResponse? status, DateTime nowUtc)
    {
        if (status is { IsDown: true })
        {
            return new SapOutageBannerText(
                true,
                status.Cause == "SwitchedOff"
                    ? $"The SAP connection is turned off{Since(status.SinceUtc, nowUtc)}"
                    : $"SAP is unavailable{Since(status.SinceUtc, nowUtc)}",
                Sentence(status.Explanation),
                status.SalesAwaitingSap > 0
                    ? $"Sales carry on and are kept here. {Sales(status.SalesAwaitingSap)} recorded since then will post to SAP when it returns."
                    : "Sales carry on and are kept here, and post to SAP when it returns. Stock transfers and van loads have to wait.");
        }

        if (status is { EndedAtUtc: not null })
        {
            return new SapOutageBannerText(
                false,
                $"SAP is back{Since(status.EndedAtUtc, nowUtc)}",
                null,
                status.SalesAwaitingSap > 0
                    ? $"{Sales(status.SalesAwaitingSap)} from the outage {(status.SalesAwaitingSap == 1 ? "is" : "are")} still waiting to post. Van sales post at the end of the day as usual."
                    : "Everything recorded during the outage has posted.");
        }

        return null;
    }

    private static string Since(DateTime? utc, DateTime nowUtc) => utc is { } at ? $" since {Cat(at, nowUtc)}" : string.Empty;

    private static string Sales(int count) => count == 1 ? "1 sale" : $"{count:N0} sales";

    // Today's outage by the clock alone; one that began on an earlier day says which.
    private static string Cat(DateTime utc, DateTime nowUtc)
    {
        var cat = IAuditService.ToCAT(utc);
        return cat.Date == IAuditService.ToCAT(nowUtc).Date
            ? cat.ToString("HH:mm")
            : cat.ToString("ddd d MMM HH:mm");
    }

    /// <summary>"planned maintenance until 03:00 (HANA backup)" as "Planned maintenance until 03:00 (HANA backup)."</summary>
    private static string? Sentence(string? explanation)
    {
        var text = explanation?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        text = char.ToUpperInvariant(text[0]) + text[1..];
        return text[^1] is '.' or '!' or '?' or '…' ? text : text + ".";
    }
}
