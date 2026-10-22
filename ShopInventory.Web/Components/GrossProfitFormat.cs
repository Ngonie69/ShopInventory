using System.Globalization;

namespace ShopInventory.Web.Components;

/// <summary>
/// How every sales report writes SAP's booked gross profit: a real minus sign, a margin to one place,
/// and a word beside it so a loss is never told by colour alone.
/// </summary>
public static class GrossProfitFormat
{
    /// <summary>The class a figure is drawn in: <c>is-loss</c>, <c>is-profit</c> or <c>is-even</c>.</summary>
    public static string Tone(decimal value) => value < 0 ? "is-loss" : value > 0 ? "is-profit" : "is-even";

    /// <summary>Loss, Profit or Break-even.</summary>
    public static string Verdict(decimal margin) => margin < 0 ? "Loss" : margin > 0 ? "Profit" : "Break-even";

    /// <summary>Money to two places with a real minus sign.</summary>
    public static string Money(decimal value) =>
        (value < 0 ? "−" : "") + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>A margin to one place with a real minus sign.</summary>
    public static string Percent(decimal value) =>
        (value < 0 ? "−" : "") + Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// One line for a tooltip or readout, such as "GP 42.00 · 28.0% margin", or "GP not in SAP yet".
    /// </summary>
    public static string Line(decimal? grossProfit, decimal? marginPercent) =>
        grossProfit is not { } gp
            ? "GP not in SAP yet"
            : marginPercent is { } margin ? $"GP {Money(gp)} · {Percent(margin)} margin" : $"GP {Money(gp)}";

    /// <summary>
    /// What the profit was measured on when only some of a row's sales are in SAP, such as "on 12 of 15
    /// sales"; null when all of them are.
    /// </summary>
    public static string? PartOf(int costedSales, int sales) =>
        costedSales > 0 && costedSales < sales ? $"on {costedSales:N0} of {sales:N0} sales" : null;
}
