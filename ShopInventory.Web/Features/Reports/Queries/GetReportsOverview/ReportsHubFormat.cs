using System.Globalization;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>How the Reports Hub writes money, counts and changes.</summary>
public static class ReportsHubFormat
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Whole US dollars: the hub reads at a glance, and the full reports keep the cents.</summary>
    public static string Usd(decimal amount) =>
        (amount < 0 ? "-$" : "$") + Math.Abs(Math.Round(amount, 0, MidpointRounding.AwayFromZero)).ToString("N0", Culture);

    public static string Zig(decimal amount) =>
        "ZiG " + Math.Round(amount, 0, MidpointRounding.AwayFromZero).ToString("N0", Culture);

    /// <summary>A chart axis figure: $950, $24k, $1.2m.</summary>
    public static string UsdShort(decimal amount) => amount switch
    {
        >= 1_000_000m => "$" + (amount / 1_000_000m).ToString("0.#", Culture) + "m",
        >= 1_000m => "$" + (amount / 1_000m).ToString("0.#", Culture) + "k",
        _ => "$" + amount.ToString("0", Culture)
    };

    public static string Count(int value) => value.ToString("N0", Culture);

    public static string Count(decimal value) => value.ToString("N0", Culture);

    public static string Percent(decimal value) => value.ToString("0.0", Culture) + "%";

    public static string Plural(int count, string one, string many) => count == 1 ? one : many;

    /// <summary>The change from <paramref name="previous"/> to <paramref name="current"/> in percent, or null when there is nothing to compare with.</summary>
    public static decimal? Change(decimal current, decimal? previous) =>
        previous is > 0 ? Math.Round((current - previous.Value) / previous.Value * 100m, 1) : null;
}
